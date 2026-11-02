import json
from pathlib import Path
import sys
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from training_core import compact_evidence, promotion_allowed, semantic_score, split_examples, target, validated_example
from build_verified_fixtures import fixtures


class VerifiedTrainingTests(unittest.TestCase):
    def test_long_evidence_keeps_verbatim_current_role_and_degree_context(self):
        example = next(fixtures())
        long = dict(example, sourceText=example['sourceText'] + ('\nUnrelated responsibilities for historic projects.' * 100))
        compact = compact_evidence(long, lambda row: len(row['sourceText']) < 1200)
        self.assertIsNotNone(compact)
        self.assertIn('April 2021 - Current', compact['sourceText'])
        self.assertEqual(compact['reviewedFields'], example['reviewedFields'])
        for line in compact['sourceText'].splitlines():
            self.assertIn(line, long['sourceText'].splitlines())
    def test_verified_fixture_evidence(self):
        for example in fixtures():
            score, valid = semantic_score(json.dumps(target(example)), target(example), example['sourceText'])
            self.assertEqual((score, valid), (1.0, True))

    def test_source_disjoint_split_even_if_client_copies(self):
        rows = list(fixtures())
        duplicate = dict(rows[0], clientId=20)
        train, held = split_examples(rows + [duplicate])
        self.assertGreaterEqual(len(held), 4)
        self.assertFalse({x['sourceText'] for x in train} & {x['sourceText'] for x in held})
        self.assertEqual(len(train) + len(held), 32)

    def test_newly_reviewed_incidents_receive_the_cpu_training_budget(self):
        rows = list(fixtures())
        train, _ = split_examples(rows)
        recent = train[-1]['sourceText']
        rows = [dict(row, _reviewedUtc='2026-11-02T00:00:00Z' if row['sourceText'] == recent else '') for row in rows]
        train, _ = split_examples(rows)
        self.assertEqual(train[0]['sourceText'], recent)

    def test_raw_ai_output_and_unsupported_facts_rejected(self):
        for changed in ({'verified': False}, {'verificationKind': 'ai_output'},
                        {'reviewedFields': {'currentCompany': 'Invented Company'}},
                        {'reviewedFields': {'email': 'hidden@example.test'}}):
            with self.assertRaises(ValueError):
                validated_example(dict(next(fixtures()), **changed))

    def test_conflicting_reviews_of_identical_source_are_not_arbitrarily_selected(self):
        rows = list(fixtures())
        other = dict(rows[0], clientId=20, reviewedFields=dict(rows[0]['reviewedFields'], currentCompany='Previous Example Company'))
        train, held = split_examples(rows + [other])
        self.assertNotIn(rows[0]['sourceText'], {row['sourceText'] for row in train + held})

    def test_quality_gate_never_promotes_regression_or_invalid_json(self):
        baseline = [(1.0, True), (0.5, True)]
        self.assertFalse(promotion_allowed(baseline, [(0.75, True), (1.0, True)], 1.0, .9))
        self.assertFalse(promotion_allowed(baseline, [(1.0, True), (1.0, False)], 1.0, .9))
        self.assertFalse(promotion_allowed(baseline, baseline, 1.0, 1.0))
        self.assertFalse(promotion_allowed(baseline, [(1.0, True), (1.0, True)], float('inf'), .5))
        self.assertTrue(promotion_allowed(baseline, [(1.0, True), (1.0, True)], 1.0, .99))

    def test_hallucination_and_skill_as_company_do_not_pass(self):
        example = next(fixtures())
        result = dict(target(example), currentCompany='Artificial intelligence design')
        score, valid = semantic_score(json.dumps(result), target(example), example['sourceText'])
        self.assertTrue(valid)
        self.assertLess(score, 1)
        result['currentTitle'] = 'Imaginary Role'
        self.assertEqual(semantic_score(json.dumps(result), target(example), example['sourceText']), (0, False))


if __name__ == '__main__':
    unittest.main()
