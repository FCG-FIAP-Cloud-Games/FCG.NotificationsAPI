"""Offline checks only: no Docker, RabbitMQ or SMTP calls."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('functional_tool', Path(__file__).with_name('functional-test.py'))
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


class FunctionalToolTests(unittest.TestCase):
    def setUp(self):
        temp_root = Path(__file__).resolve().parent / 'artifacts' / 'offline-tests'
        temp_root.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=temp_root)
        assert Path(self.temp.name).resolve().is_relative_to(temp_root.resolve())
        self.addCleanup(self.temp.cleanup)
        self.art = patch.object(tool, 'ART', Path(self.temp.name))
        self.art.start()
        self.addCleanup(self.art.stop)
        self.output = patch('builtins.print')
        self.output.start()
        self.addCleanup(self.output.stop)
        self.event = tool.make_event('recipient@example.test')
        self.raw = json.dumps(self.event).encode()
        self.digest = tool.hashlib.sha256(self.raw).hexdigest()
        self.api = {'State': {'Running': True}, 'Config': {'Env': [
            'NotificationsDispatcher__Enabled=true', 'RabbitMq__Host=rabbitmq',
            'RabbitMq__VirtualHost=/dispatcher-rehearsal']}}

    def test_contract_and_immutable_hash(self):
        self.assertEqual(set(self.event), {'EventId', 'CorrelationId', 'OccurredAt', 'Version',
            'UserId', 'OrderId', 'GameId', 'Amount', 'Currency', 'Email'})
        (tool.ART / 'event.json').write_bytes(self.raw)
        (tool.ART / 'event.sha256').write_text(self.digest)
        self.assertEqual(tool.load_event()[0], self.raw)
        (tool.ART / 'event.json').write_bytes(self.raw + b' ')
        with self.assertRaises(RuntimeError):
            tool.load_event()

    def test_no_confirmation_no_network(self):
        with patch.object(tool, 'http') as http, patch.object(tool, 'inspect') as inspect:
            with self.assertRaises(RuntimeError):
                tool.publish(self.raw, self.event, self.digest, False, False)
            http.assert_not_called()
            inspect.assert_not_called()

    def test_lost_response_does_not_retry(self):
        requests = []
        def fake_http(path, body=None):
            if body is not None:
                requests.append(body)
                raise RuntimeError('Simulated lost response')
            return {'consumers': 1}
        with patch.object(tool, 'inspect', return_value=self.api), patch.object(tool, 'http', side_effect=fake_http), \
             patch.object(tool, 'database', return_value={'inbox': [], 'deliveries': []}):
            with self.assertRaises(RuntimeError):
                tool.publish(self.raw, self.event, self.digest, False, True)
            with self.assertRaises(FileExistsError):
                tool.publish(self.raw, self.event, self.digest, False, True)
        self.assertEqual(len(requests), 1)

    def test_repeat_preserves_payload_and_message_id(self):
        requests = []
        def fake_http(path, body=None):
            if body is not None:
                requests.append(json.loads(body))
                return {'routed': True}
            return {'consumers': 1}
        baseline = {'test': 'baseline'}
        with patch.object(tool, 'inspect', return_value=self.api), patch.object(tool, 'http', side_effect=fake_http), \
             patch.object(tool, 'database', return_value={'inbox': [], 'deliveries': []}), \
             patch.object(tool, 'require_sent'), patch.object(tool, 'snapshot', return_value=baseline):
            tool.publish(self.raw, self.event, self.digest, False, True)
            (tool.ART / 'baseline.json').write_text(json.dumps(baseline))
            tool.publish(self.raw, self.event, self.digest, True, True)
        self.assertEqual(requests[0], requests[1])
        self.assertEqual(requests[0]['payload'].encode(), self.raw)
        self.assertEqual(requests[0]['properties']['message_id'], self.event['EventId'])


if __name__ == '__main__':
    unittest.main()
