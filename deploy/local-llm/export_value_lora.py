"""Export the one supported v-projection LoRA without original weight shards.

Tensor names/metadata follow pinned llama.cpp's LoRA writer/adapter loader.
There is no q/k rotary permutation or DeltaNet packing for this projection.
"""
import json
from pathlib import Path
import re


def export_value_lora(directory):
    import gguf
    import numpy as np
    from safetensors.numpy import load_file
    directory = Path(directory)
    config = json.loads((directory / 'adapter_config.json').read_text())
    if config['r'] != 4 or config['lora_alpha'] != 8 or config['bias'] != 'none' or config['target_modules'] != ['model.layers.31.self_attn.v_proj']:
        raise ValueError('Exporter accepts only the verified fixed v-projection adapter')
    weights = load_file(str(directory / 'adapter_model.safetensors'))
    tensors = {}
    for name, value in weights.items():
        match = re.fullmatch(r'base_model\.model\.model\.(?:language_model\.)?layers\.31\.self_attn\.v_proj\.lora_([AB])\.weight', name)
        if not match or value.dtype != np.float32 or not np.isfinite(value).all():
            raise ValueError('Unsupported adapter tensor')
        key = match[1]
        expected = (4, 2560) if key == 'A' else (1024, 4)
        if value.shape != expected or key in tensors:
            raise ValueError('Adapter rank or dimensions do not match the serving model')
        tensors[key] = value
    if set(tensors) != {'A', 'B'} or not np.count_nonzero(tensors['B']):
        raise ValueError('Trained adapter pair missing')
    path = directory / 'adapter.gguf'
    temporary = directory / 'adapter.gguf.tmp'
    writer = gguf.GGUFWriter(temporary, 'qwen35')
    try:
        writer.add_type(gguf.GGUFType.ADAPTER)
        writer.add_string(gguf.Keys.Adapter.TYPE, 'lora')
        writer.add_float32(gguf.Keys.Adapter.LORA_ALPHA, 8.0)
        for kind, value in sorted(tensors.items()):
            writer.add_tensor('blk.31.attn_v.weight.lora_' + kind.lower(), value)
        writer.write_header_to_file(); writer.write_kv_data_to_file(); writer.write_tensors_to_file()
    finally:
        writer.close()
    temporary.replace(path)
    return path
