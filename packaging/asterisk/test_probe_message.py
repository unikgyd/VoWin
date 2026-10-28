"""Offline safety tests: the live probe never REGISTERs, calls, or retries SMS."""
import contextlib
import io
import json
from pathlib import Path
import re
import runpy
import socket
import sys
import unittest
from unittest.mock import patch


SOURCE = Path(__file__).with_name('probe_message.py')


class ProbeTests(unittest.TestCase):
    def run_probe(self, destination, allow_sms=False, challenge=False, timeout=False, expected_exit=0):
        sent = []
        self.last_sent = sent

        class FakeSocket:
            def __enter__(self):
                return self

            def __exit__(self, *args):
                pass

            def bind(self, endpoint):
                self.endpoint = endpoint

            def getsockname(self):
                return ('192.168.70.2', 34567)

            def settimeout(self, seconds):
                pass

            def sendto(self, packet, endpoint):
                sent.append(packet)

            def recvfrom(self, size):
                if timeout:
                    raise socket.timeout('Synthetic uncertain submission')
                request = sent[-1].decode()
                code = 401 if challenge and len(sent) == 1 else 202
                call_id = re.search(r'Call-ID: ([^\r]+)', request)[1]
                cseq = re.search(r'CSeq: ([^\r]+)', request)[1]
                response = f'SIP/2.0 {code} Test\r\nCall-ID: {call_id}\r\nCSeq: {cseq}\r\n'
                if code == 401:
                    response += 'WWW-Authenticate: Digest realm="test", nonce="123", algorithm=MD5, qop="auth"\r\n'
                return (response + 'Content-Length: 0\r\n\r\n').encode(), ('192.168.70.2', 5060)

        config = {'ServerIp': '192.168.70.2', 'Extensions': ['desk-a', 'desk-b']}
        argv = [str(SOURCE), '--config', 'deployment.json', '--credentials', 'credentials.json',
                '--account', 'desk-a', '--destination', destination, '--text', 'Synthetic probe']
        if allow_sms:
            argv.append('--allow-real-sms')
        result = io.StringIO()
        read = lambda path, *args, **kwargs: json.dumps(
            config if path.name == 'deployment.json' else {'desk-a': 'never-print-this-secret'})
        with patch.object(sys, 'argv', argv), patch.object(Path, 'read_text', read), \
                patch.object(socket, 'socket', return_value=FakeSocket()), contextlib.redirect_stdout(result):
            try:
                runpy.run_path(str(SOURCE), run_name='__main__')
            except SystemExit as ex:
                self.assertEqual(expected_exit, ex.code)
        return sent, result.getvalue()

    def test_local_message_does_not_register_or_call(self):
        sent, output = self.run_probe('desk-b')
        self.assertEqual(1, len(sent))
        self.assertTrue(sent[0].startswith(b'MESSAGE sip:desk-b@'))
        self.assertTrue(json.loads(output)['localDestination'])

    def test_carrier_target_requires_explicit_opt_in(self):
        with contextlib.redirect_stderr(io.StringIO()):
            sent, _ = self.run_probe('+15550000000', expected_exit=2)
        self.assertEqual([], sent)

    def test_explicit_sms_target_is_sent_once(self):
        sent, output = self.run_probe('+15550000000', allow_sms=True)
        self.assertEqual(1, len(sent))
        self.assertFalse(json.loads(output)['localDestination'])

    def test_digest_challenge_has_one_authenticated_attempt_and_no_password_output(self):
        sent, output = self.run_probe('desk-b', challenge=True)
        self.assertEqual(2, len(sent))
        self.assertNotIn(b'Authorization:', sent[0])
        self.assertIn(b'Authorization: Digest ', sent[1])
        self.assertNotIn('never-print-this-secret', output)
        self.assertNotIn(b'never-print-this-secret', sent[1])
        self.assertTrue(all(packet.startswith(b'MESSAGE ') for packet in sent))

    def test_uncertain_submission_is_not_retried(self):
        with self.assertRaises(socket.timeout):
            self.run_probe('+15550000000', allow_sms=True, timeout=True)
        self.assertEqual(1, len(self.last_sent))


if __name__ == '__main__':
    unittest.main()
