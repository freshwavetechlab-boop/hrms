import json
from pathlib import Path
import sys
import tempfile
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from export_value_lora import export_value_lora


class LoraExportTests(unittest.TestCase):
    def test_real_gguf_metadata_shapes_and_both_saved_prefixes(self):
        import gguf
        import numpy as np
        from safetensors.numpy import save_file
        for prefix in ('base_model.model.model.', 'base_model.model.model.language_model.'):
            with tempfile.TemporaryDirectory(prefix='verified-lora-export-') as root:
                directory = Path(root)
                (directory / 'adapter_config.json').write_text(json.dumps({'r': 4, 'lora_alpha': 8, 'bias': 'none',
                    'target_modules': ['model.layers.31.self_attn.v_proj']}))
                name = prefix + 'layers.31.self_attn.v_proj.lora_'
                save_file({name + 'A.weight': np.ones((4, 2560), dtype=np.float32),
                           name + 'B.weight': np.full((1024, 4), .001, dtype=np.float32)}, str(directory / 'adapter_model.safetensors'))
                output = export_value_lora(directory)
                reader = gguf.GGUFReader(output)
                self.assertEqual(reader.fields['general.type'].contents(), 'adapter')
                self.assertEqual(reader.fields['general.architecture'].contents(), 'qwen35')
                self.assertEqual(reader.fields['adapter.type'].contents(), 'lora')
                self.assertEqual(float(reader.fields['adapter.lora.alpha'].contents()), 8)
                self.assertEqual({t.name for t in reader.tensors}, {'blk.31.attn_v.weight.lora_a', 'blk.31.attn_v.weight.lora_b'})
                self.assertEqual([list(t.shape) for t in reader.tensors], [[2560, 4], [4, 1024]])
                del reader

    def test_wrong_target_cannot_be_exported_with_unapplied_layout_transform(self):
        with tempfile.TemporaryDirectory(prefix='verified-lora-export-') as root:
            directory = Path(root)
            (directory / 'adapter_config.json').write_text(json.dumps({'r': 4, 'lora_alpha': 8, 'bias': 'none',
                'target_modules': ['model.layers.31.self_attn.q_proj']}))
            with self.assertRaises(ValueError):
                export_value_lora(directory)


if __name__ == '__main__':
    unittest.main()
