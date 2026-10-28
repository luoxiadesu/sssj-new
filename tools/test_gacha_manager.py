import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import gacha_manager as manager


class ManagerTests(unittest.TestCase):
    def setUp(self):
        self.pools = [dict(type=1, id=1, segment=1), dict(type=1, id=1, segment=2), dict(type=0, id=1, segment=1)]

    def test_time_is_explicitly_beijing(self):
        self.assertEqual('1970-01-01 08:00', manager.display_time(0))

    def test_overlap_rejected_same_id_across_segments(self):
        entries = [dict(type=1, id=1, segment=1, start=100, end=201),
                   dict(type=1, id=1, segment=2, start=200, end=300)]
        with self.assertRaises(ValueError):
            manager.validate(dict(enabled=True, entries=entries), self.pools)
        entries[0]['end'] = 200
        manager.validate(dict(enabled=True, entries=entries), self.pools)

    def test_atomic_save_backup_and_reload(self):
        with tempfile.TemporaryDirectory(prefix='sssj-manager-') as folder:
            root = Path(folder)
            with patch.object(manager, 'ROOT', root), patch.object(manager, 'SETTINGS', root), patch.object(manager, 'PLAN', root / 'plan.json'):
                original = dict(enabled=False, entries=[])
                self.assertIsNone(manager.save(original, self.pools))
                next_plan = dict(enabled=True, entries=[dict(type=0, id=1, segment=1, start=100, end=200)])
                backup = manager.save(next_plan, self.pools)
                self.assertEqual(original, json.loads(backup.read_text(encoding='utf-8')))
                self.assertEqual(next_plan, manager.load(self.pools))
                with self.assertRaises(ValueError):
                    manager.save(dict(enabled=True, entries=[dict(type=0, id=1, segment=1, start=200, end=100)]), self.pools)
                self.assertEqual(next_plan, manager.load(self.pools))


if __name__ == '__main__':
    unittest.main()
