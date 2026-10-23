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
        self.art_root = patch.object(tool, 'ART_ROOT', Path(self.temp.name))
        self.art_root.start()
        self.addCleanup(self.art_root.stop)
        self.no_docker = patch.object(tool, 'docker', side_effect=AssertionError('Offline test attempted Docker'))
        self.no_docker.start()
        self.addCleanup(self.no_docker.stop)
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

    def test_recipient_trims_edges_but_rejects_other_formats_without_echo(self):
        self.assertEqual(tool.make_event('  recipient@example.test\r\n')['Email'], 'recipient@example.test')
        for address in ['Name <recipient@example.test>', 'recipient @example.test', '', 'not-an-address']:
            with self.subTest(address=address), self.assertRaises(RuntimeError) as error:
                tool.make_event(address)
            if address:
                self.assertNotIn(address, str(error.exception))

    def test_new_scenarios_preserve_all_legacy_evidence(self):
        legacy = tool.scenario_path('legacy')
        files = {'event.json': self.raw, 'event.sha256': self.digest.encode(),
                 'baseline.json': b'{"previous":"snapshot"}', 'publish.intent.json': b'old-intent',
                 'publish.receipt.json': b'old-receipt', 'repeat.receipt.json': b'old-repeat'}
        for name, value in files.items():
            (legacy / name).write_bytes(value)
        with patch.object(tool.getpass, 'getpass', side_effect=['one@example.test', 'two@example.test']):
            tool.initialize_scenario('second')
            tool.initialize_scenario('third')
        events = [json.loads((tool.scenario_path(name) / 'event.json').read_bytes())
                  for name in ['second', 'third']]
        for key in ['EventId', 'CorrelationId', 'UserId', 'OrderId', 'GameId']:
            self.assertNotEqual(events[0][key], events[1][key])
        for name, value in files.items():
            self.assertEqual((legacy / name).read_bytes(), value)

    def test_existing_scenario_rejected_before_prompt_and_never_overwritten(self):
        with patch.object(tool.getpass, 'getpass', return_value='one@example.test'):
            tool.initialize_scenario('second')
        path = tool.scenario_path('second')
        before = {f.name: f.read_bytes() for f in path.iterdir()}
        with patch.object(tool.getpass, 'getpass') as prompt:
            for name in ['second', 'legacy']:
                with self.assertRaisesRegex(RuntimeError, 'nothing was overwritten'):
                    tool.initialize_scenario(name)
            prompt.assert_not_called()
        self.assertEqual({f.name: f.read_bytes() for f in path.iterdir()}, before)

    def test_scenario_paths_reject_traversal_and_windows_reserved_names(self):
        for name in ['../legacy', 'a/b', 'a\\b', 'C:\\temp', '.', '', 'CON', 'nul', 'com1', 'lpt9', 'a'*49]:
            with self.subTest(name=name), self.assertRaises(RuntimeError):
                tool.scenario_path(name)

    def test_invalid_recipient_does_not_reserve_scenario(self):
        with patch.object(tool.getpass, 'getpass', return_value='invalid'), self.assertRaises(RuntimeError):
            tool.initialize_scenario('second')
        self.assertFalse(tool.scenario_path('second').exists())

    def test_cli_requires_explicit_selection_and_confirmation_before_network(self):
        with patch.object(tool.sys, 'argv', ['tool', 'publish', '--confirm-send']), \
             patch('sys.stderr'), self.assertRaises(SystemExit):
            tool.main()
        with patch.object(tool.getpass, 'getpass', return_value='one@example.test'):
            tool.initialize_scenario('second')
        with patch.object(tool.sys, 'argv', ['tool', 'publish', '--scenario', 'second']), \
             patch.object(tool, 'isolation') as network, self.assertRaises(RuntimeError):
            tool.main()
        network.assert_not_called()

    def test_cli_routes_plan_snapshot_compare_publish_repeat_to_selected_scenario(self):
        with patch.object(tool.getpass, 'getpass', side_effect=['one@example.test', 'two@example.test']):
            tool.initialize_scenario('second')
            tool.initialize_scenario('third')
        for name in ['second', 'third']:
            path = tool.scenario_path(name)
            event = json.loads((path / 'event.json').read_bytes())
            state = {'event_id': event['EventId']}
            with patch.object(tool, 'isolation'), patch.object(tool, 'snapshot', return_value=state) as snap, \
                 patch.object(tool, 'require_sent'), patch.object(tool, 'publish') as publish:
                for action in ['plan', 'snapshot', 'compare', 'publish', 'repeat']:
                    with patch.object(tool.sys, 'argv', ['tool', action, '--scenario', name, '--confirm-send']):
                        tool.main()
                    self.assertEqual(tool.ART, path)
                self.assertEqual(json.loads((path / 'baseline.json').read_text()), state)
                for call in snap.call_args_list:
                    self.assertEqual(call.args[0], event)
                for call in publish.call_args_list:
                    self.assertEqual(call.args[0], (path / 'event.json').read_bytes())
                    self.assertEqual(call.args[1], event)
                self.assertEqual([call.args[3] for call in publish.call_args_list], [False, True])

    def test_publication_and_repeat_receipts_are_independent_between_scenarios(self):
        requests = []
        def fake_http(path, body=None):
            if body is not None:
                requests.append(json.loads(body))
                return {'routed': True}
            return {'consumers': 1}
        with patch.object(tool.getpass, 'getpass', side_effect=['one@example.test', 'two@example.test']):
            tool.initialize_scenario('second')
            tool.initialize_scenario('third')
        for name in ['second', 'third']:
            with patch.object(tool, 'ART', tool.scenario_path(name)), \
                 patch.object(tool, 'inspect', return_value=self.api), patch.object(tool, 'http', side_effect=fake_http), \
                 patch.object(tool, 'database', return_value={'inbox': [], 'deliveries': []}), \
                 patch.object(tool, 'snapshot', return_value={}), patch.object(tool, 'require_sent'):
                raw, event, digest = tool.load_event()
                tool.publish(raw, event, digest, False, True)
                (tool.ART / 'baseline.json').write_text('{}')
                tool.publish(raw, event, digest, True, True)
                self.assertEqual(json.loads((tool.ART / 'repeat.receipt.json').read_text())['sha256'], digest)
        self.assertEqual(requests[0], requests[1])
        self.assertEqual(requests[2], requests[3])
        self.assertNotEqual(requests[0]['properties']['message_id'], requests[2]['properties']['message_id'])


if __name__ == '__main__':
    unittest.main()
