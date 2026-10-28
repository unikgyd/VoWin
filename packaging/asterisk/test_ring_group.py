"""Offline generator tests; --isolated also tests the real PBX in a network-none container.

The isolated suite never connects to the deployed PBX, real phones or VoWin.
It uses the existing local image only, synthetic credentials and loopback SIP.
"""
import contextlib
import base64
import hashlib
import io
import json
import os
from pathlib import Path
import re
import runpy
import select
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch
import uuid


SOURCE = Path(__file__).with_name('configure.py')


def generate(directory, messages=False):
    script = directory / 'configure.py'
    shutil.copyfile(SOURCE, script)
    # These are deliberately test fixtures, not generator defaults.
    deployment = directory / 'deployment.local.json'
    deployment.write_text(json.dumps({
        'ServerIp': '127.0.0.1', 'WindowsIp': '127.0.0.1', 'Realm': 'vowin.local',
        'TrunkUsername': 'hostims', 'IncomingTarget': '600', 'EchoExtension': '700',
        'Extensions': ['1001', '1002'], 'Lines': [{'Id': 'com6'}, {'Id': 'com10'}],
        'MessagesEnabled': messages,
        'DialRules': [
            {'Pattern': '_1[3-9]XXXXXXXXX', 'Strip': 0, 'Prepend': '+86', 'LineId': 'com6'},
            {'Pattern': '_81X.', 'Strip': 2, 'Prepend': '+', 'LineId': 'com6'},
            {'Pattern': '_82X.', 'Strip': 2, 'Prepend': '+', 'LineId': 'com10'}]
    }))
    argv = [str(script), '--config', str(deployment), '--output', str(directory)]
    with patch.object(sys, 'argv', argv), contextlib.redirect_stdout(io.StringIO()):
        runpy.run_path(str(script), run_name='__main__')
    return directory / 'config'


def mt_readiness_probe(port):
    # Independent fixture for SipGateway.ProbeSmsPeerAsync. In particular, this
    # is NOT a MESSAGE, never authenticates, and contains no SMS-derived data.
    return Sip('OPTIONS sip:keepalive@127.0.0.1:5060 SIP/2.0', {
        'via': f'SIP/2.0/UDP 127.0.0.1:{port};branch=z9hG4bK{uuid.uuid4().hex};rport',
        'from': f'<sip:gateway@vowin.local>;tag={uuid.uuid4().hex}',
        'to': '<sip:keepalive@127.0.0.1:5060>',
        'call-id': uuid.uuid4().hex + '@vowin.local',
        'cseq': '1 OPTIONS', 'max-forwards': '20'})


class GeneratorTests(unittest.TestCase):
    def test_mt_readiness_fixture_has_no_sms_payload_identity_or_authorization(self):
        request = mt_readiness_probe(5062)
        self.assertEqual('OPTIONS sip:keepalive@127.0.0.1:5060 SIP/2.0', request.start)
        self.assertEqual('', request.body)
        self.assertEqual('<sip:keepalive@127.0.0.1:5060>', request.headers['to'])
        self.assertRegex(request.headers['from'], r'^<sip:gateway@vowin\.local>;tag=[0-9a-f]{32}$')
        self.assertEqual('1 OPTIONS', request.headers['cseq'])
        self.assertEqual('20', request.headers['max-forwards'])
        self.assertFalse({'authorization', 'proxy-authorization', 'contact', 'x-vowin-line', 'content-type'} & request.headers.keys())
        self.assertIn(b'content-length: 0\r\n', request.bytes())

    def test_sim_bound_and_legacy_reply_aliases_reach_gateway_without_default_line(self):
        with tempfile.TemporaryDirectory(prefix='vowin-sim-reply-unit-') as temporary, patch.object(os, 'chown', create=True):
            config = generate(Path(temporary), messages=True)
            text = (config / 'extensions.conf').read_text()
            for pattern in ('_sms-.', '_sms2-.'):
                route = text.split('exten => ' + pattern + ',1,', 1)[1].split('exten => ', 1)[0]
                self.assertIn('Set(MESSAGE(custom_data)=clear_all_outbound)', route)
                self.assertIn('MessageSend(pjsip:PJSIP/${EXTEN}@vowin,${MESSAGE(from)},sip:${EXTEN}@127.0.0.1:5062)', route)
                self.assertIn('GotoIf($[${VOWIN_ALIAS_VALID} = 1]?send)', route)
                self.assertNotIn('X-VoWin-Line', route)
                self.assertNotIn('Dial(', route)
                self.assertNotIn('${EXTEN:', route)
            self.assertIn('[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]-[+]?[0-9]{7,15}$', text)

    def test_message_target_guard_uses_to_only_for_exact_comparison_never_routing(self):
        with tempfile.TemporaryDirectory(prefix='vowin-sim-target-unit-') as temporary, patch.object(os, 'chown', create=True):
            text = (generate(Path(temporary), messages=True) / 'extensions.conf').read_text()
            guard = text.split('[check-phone-message-target]', 1)[1].split('[phone-messages-', 1)[0]
            self.assertIn('Set(VOWIN_MESSAGE_TO=${MESSAGE(to)})', guard)
            self.assertIn('PJSIP_PARSE_URI_FROM(VOWIN_TO_URI,user)', guard)
            self.assertIn('^[A-Za-z0-9_+.-]{1,78}$', guard)
            self.assertIn('GotoIf($["${VOWIN_TO_USER}" != "${VOWIN_REQUEST_USER}"]?reject)', guard)
            self.assertIn('n(reject),Hangup()', guard)
            self.assertNotIn('MessageSend(', guard)
            self.assertNotIn('MESSAGE_DATA(Request-URI)', text)
            for extension in ('1001', '1002'):
                entry = text.split('[phone-messages-' + extension + ']', 1)[1].split('[', 1)[0]
                self.assertIn('Set(VOWIN_REQUEST_USER=${EXTEN})', entry)
                self.assertIn('Gosub(check-phone-message-target,s,1)', entry)
            self.assertNotIn('PJSIP/${VOWIN_TO_USER}', text)

    def test_bare_chinese_mobile_route_is_narrow_and_keeps_explicit_prefixes(self):
        with tempfile.TemporaryDirectory(prefix='vowin-mobile-unit-') as temporary:
            with patch.object(os, 'chown', create=True):
                config = generate(Path(temporary))
            text = (config / 'extensions.conf').read_text()
            phones = text.split('[from-phones]', 1)[1].split('[set-vowin-line]', 1)[0]
            self.assertIn('exten => _1[3-9]XXXXXXXXX,1,Dial(PJSIP/+86${EXTEN}@vowin,60,b(set-vowin-line^s^1(com6)))', phones)
            self.assertIn('exten => _81X.,1,Dial(PJSIP/+${EXTEN:2}@vowin,60,b(set-vowin-line^s^1(com6)))', phones)
            self.assertIn('exten => _82X.,1,Dial(PJSIP/+${EXTEN:2}@vowin,60,b(set-vowin-line^s^1(com10)))', phones)
            self.assertNotIn('exten => _X.', phones)
            self.assertNotIn('exten => _.', phones)
            self.assertIn('exten => 1001,1,Set(VOWIN_CONTACTS=${PJSIP_DIAL_CONTACTS(1001)})', phones)
            self.assertIn('exten => 1002,1,Set(VOWIN_CONTACTS=${PJSIP_DIAL_CONTACTS(1002)})', phones)
            self.assertEqual(2, phones.count('Dial(${VOWIN_CONTACTS},30)'))
            self.assertEqual(2, phones.count('n(unavailable),Hangup(20)'))
            self.assertNotIn('Dial(PJSIP/1001,', phones)
            self.assertNotIn('Dial(PJSIP/1002,', phones)

    def test_both_lines_share_one_ring_group_and_all_aor_contacts(self):
        with tempfile.TemporaryDirectory(prefix='vowin-ring-unit-') as temporary:
            with patch.object(os, 'chown', create=True):
                config = generate(Path(temporary))
            text = (config / 'extensions.conf').read_text()
            incoming = text.split('[from-vowin]', 1)[1].split('[from-phones]', 1)[0]
            self.assertIn('"com6"]?ring)', incoming)
            self.assertIn('"com10"]?ring)', incoming)
            self.assertEqual(1, incoming.count('Dial('))
            self.assertIn('Dial(${VOWIN_RING},30)', incoming)
            self.assertIn('${PJSIP_DIAL_CONTACTS(1001)}', incoming)
            self.assertIn('${PJSIP_DIAL_CONTACTS(1002)}', incoming)
            self.assertIn('${VOWIN_RING}&${VOWIN_NEXT}', incoming)
            self.assertIn('n(unavailable),Hangup(20)', incoming)
            self.assertNotIn('PJSIP_DIAL_CONTACTS(vowin)', incoming)
            self.assertNotIn('ExecIf(', incoming)  # app_exec is not in the minimal image's module allowlist.
            self.assertNotRegex(incoming, r'(?:,|\))Answer\(')
            self.assertIn('set-vowin-line^s^1(com6)', text)
            self.assertIn('set-vowin-line^s^1(com10)', text)
            self.assertIn('load=res_pjsip_caller_id.so', (config / 'modules.conf').read_text())
            self.assertIn('load=res_pjsip_nat.so', (config / 'modules.conf').read_text())

    def test_regeneration_preserves_credentials_and_opt_in_outbound(self):
        with tempfile.TemporaryDirectory(prefix='vowin-ring-unit-') as temporary:
            directory = Path(temporary)
            with patch.object(os, 'chown', create=True):
                generate(directory)
                before = (directory / 'credentials.json').read_bytes()
                generate(directory)
            self.assertEqual(before, (directory / 'credentials.json').read_bytes())
            profile = json.loads((directory / 'vowin-client.json').read_text())
            self.assertFalse(profile['Settings']['AllowOutboundCalls'])

    def test_arbitrary_accounts_ports_lines_and_firewall_have_no_lab_defaults(self):
        generator = runpy.run_path(str(SOURCE), run_name='test_configure')
        values = {'ServerIp': '192.168.77.2', 'WindowsIp': '192.168.77.19', 'BindAddress': '0.0.0.0',
                  'SipPort': 5070, 'WindowsPort': 5072, 'Realm': 'example.local', 'TrunkUsername': 'my-gateway',
                  'IncomingTarget': '800', 'Extensions': ['desk-a', 'desk-b', 'desk-c'],
                  'Lines': [{'Id': 'my-modem'}], 'DialRules': [
                      {'Pattern': '_71X.', 'Strip': 2, 'Prepend': '+', 'LineId': 'my-modem'}],
                  'TrustedNetworks': ['192.168.77.0/24'], 'MessagesEnabled': True,
                  'MessageDomain': 'pbx.example.local'}
        with tempfile.TemporaryDirectory(prefix='vowin-generic-unit-') as temporary, patch.object(os, 'chown', create=True):
            output = Path(temporary)
            generator['generate'](values, output)
            pjsip = (output / 'config/pjsip.conf').read_text()
            self.assertIn('bind=0.0.0.0:5070', pjsip)
            self.assertIn('192.168.77.19:5072', pjsip)
            self.assertIn('username=my-gateway', pjsip)
            self.assertNotIn('com6', pjsip)
            dialplan = (output / 'config/extensions.conf').read_text()
            self.assertIn('exten => desk-c,1,', dialplan)
            self.assertIn('phone-messages-desk-a', dialplan)
            self.assertIn('Set(MESSAGE(from)=sip:desk-a@pbx.example.local:5070)', dialplan)
            self.assertIn('sip:${ARG1}@pbx.example.local:5070', dialplan)
            self.assertIn('PJSIP_PARSE_URI(${MESSAGE(from)},display)', dialplan)
            self.assertIn('load=res_pjsip_messaging.so', (output / 'config/modules.conf').read_text())
            self.assertIn('192.168.77.0/24', (output / 'vowin-sip-guard.nft').read_text())
            self.assertNotIn('br0', (output / 'vowin-sip-guard.nft').read_text())
            exported = json.loads((output / 'vowin-client.json').read_text())['Settings']
            self.assertFalse(exported['AllowOutgoingSms']); self.assertFalse(exported['ForwardIncomingSms'])

    def test_unsafe_input_is_rejected_before_any_credentials_are_created(self):
        generator = runpy.run_path(str(SOURCE), run_name='test_configure')
        values = {'ServerIp': '127.0.0.1', 'WindowsIp': '127.0.0.1', 'Realm': 'test.local',
                  'TrunkUsername': 'gateway', 'IncomingTarget': '800', 'Extensions': ['desk'],
                  'Lines': [{'Id': 'module'}], 'DialRules': []}
        for change in ({'Extensions': ['desk', 'desk']}, {'BindAddress': '0.0.0.0'}, {'WindowsIp': '8.8.8.8'},
                       {'TrunkUsername': 'bad\ninclude=x'}, {'DialRules': [{'Pattern': '_X.', 'LineId': 'module'}]},
                       {'MessageDomain': 'bad\ninclude=x'}, {'MessageDomain': '8.8.8.8'}, {'MessageDomain': '0.0.0.0'}):
            with tempfile.TemporaryDirectory(prefix='vowin-invalid-unit-') as temporary:
                output = Path(temporary) / 'generated'
                with self.assertRaises(ValueError):
                    generator['generate'](dict(values, **change), output)
                self.assertFalse(output.exists())


def command(*args):
    return subprocess.check_output(args, text=True, stderr=subprocess.STDOUT)


class Sip:
    def __init__(self, start, headers, body=''):
        self.start, self.headers, self.body = start, headers, body

    @classmethod
    def parse(cls, raw):
        head, _, body = raw.decode('utf-8').partition('\r\n\r\n')
        lines = head.split('\r\n')
        return cls(lines[0], {key.lower(): value.strip() for key, value in
                             (line.split(':', 1) for line in lines[1:])}, body)

    def bytes(self):
        headers = dict(self.headers, **{'content-length': str(len(self.body.encode()))})
        return (self.start + '\r\n' + ''.join(f'{k}: {v}\r\n' for k, v in headers.items())
                + '\r\n' + self.body).encode()

    @property
    def method(self):
        return self.start.split(' ')[0]

    @property
    def status(self):
        return int(self.start.split(' ')[1]) if self.method == 'SIP/2.0' else 0


class Phone:
    def __init__(self, name, password, port=0):
        self.name, self.password = name, password
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.socket.bind(('127.0.0.1', port))
        self.port = self.socket.getsockname()[1]
        self.tag = uuid.uuid4().hex[:10]
        self.inbox = []
        self.sequence = 0
        self.contact_host = '127.0.0.1'

    def send(self, message):
        self.socket.sendto(message.bytes(), ('127.0.0.1', 5060))

    def request(self, method, user, domain='127.0.0.1'):
        self.sequence += 1
        return Sip(f'{method} sip:{user}@{domain} SIP/2.0', {
            'via': f'SIP/2.0/UDP 127.0.0.1:{self.port};branch=z9hG4bK{uuid.uuid4().hex};rport',
            'from': f'<sip:{self.name}@127.0.0.1>;tag={self.tag}',
            'to': f'<sip:{user}@{domain}>', 'call-id': uuid.uuid4().hex,
            'cseq': f'{self.sequence} {method}', 'max-forwards': '70',
            'contact': f'<sip:{self.name}@{self.contact_host}:{self.port}>'})

    def respond(self, request, code, reason, body=''):
        headers = {key: request.headers[key] for key in ('via', 'from', 'to', 'call-id', 'cseq')}
        if ';tag=' not in headers['to']:
            headers['to'] += ';tag=' + self.tag
        headers['contact'] = f'<sip:{self.name}@{self.contact_host}:{self.port}>'
        if body:
            headers['content-type'] = 'application/sdp'
        response = Sip(f'SIP/2.0 {code} {reason}', headers, body)
        self.send(response)
        return response


class IsolatedExercise:
    def __init__(self, credentials, container):
        assert container.startswith('vowin-ring-test-'), 'Only a disposable test container is allowed'
        network = json.loads(command('docker', 'inspect', container))[0]
        assert network['HostConfig']['NetworkMode'] == 'none'
        assert not network['HostConfig']['PortBindings']
        assert os.path.samefile('/proc/self/ns/net', f'/proc/{network["State"]["Pid"]}/ns/net'), \
            'Refusing to send SIP outside the isolated test network namespace'
        self.caller = Phone('hostims', credentials['hostims'], 5062)
        self.one = Phone('1001', credentials['1001'])
        self.two = Phone('1002', credentials['1002'])
        self.extra = Phone('1001', credentials['1001'])
        self.phones = [self.caller, self.one, self.two, self.extra]

    def receive(self, phone, predicate, seconds=5, required=True):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            for index, message in enumerate(phone.inbox):
                if predicate(message):
                    return phone.inbox.pop(index)
            ready, _, _ = select.select([p.socket for p in self.phones], [], [],
                                        min(.1, max(0, deadline - time.monotonic())))
            for sock in ready:
                raw, source = sock.recvfrom(65535)
                assert source == ('127.0.0.1', 5060), 'SIP left the isolated PBX'
                received = Sip.parse(raw)
                target = next(p for p in self.phones if p.socket is sock)
                if received.method == 'OPTIONS':
                    target.respond(received, 200, 'OK')
                else:
                    target.inbox.append(received)
        if not required:
            return None
        pending = [(p.name, m.start, m.headers.get('cseq')) for p in self.phones for m in p.inbox]
        raise AssertionError(f'Timed out waiting for {phone.name}; synthetic pending events: {pending}')

    def final(self, phone, request):
        return self.receive(phone, lambda m: m.status >= 200 and
                            m.headers['call-id'] == request.headers['call-id'] and
                            m.headers['cseq'] == request.headers['cseq'])

    def authenticate(self, phone, request):
        phone.send(request)
        challenge = self.final(phone, request)
        assert challenge.status == 401
        if request.method == 'INVITE':
            self.ack(phone, request, challenge, success=False)
        params = {key: quoted or bare for key, quoted, bare in re.findall(
            r'(\w+)=(?:"([^"]*)"|([^,\s]+))', challenge.headers['www-authenticate'])}
        uri = request.start.split(' ')[1]
        cnonce = uuid.uuid4().hex
        md5 = lambda text: hashlib.md5(text.encode()).hexdigest()
        ha1 = md5(f'{phone.name}:{params["realm"]}:{phone.password}')
        ha2 = md5(f'{request.method}:{uri}')
        digest = md5(f'{ha1}:{params["nonce"]}:00000001:{cnonce}:auth:{ha2}')
        request.headers['authorization'] = (f'Digest username="{phone.name}", realm="{params["realm"]}", '
            f'nonce="{params["nonce"]}", uri="{uri}", response="{digest}", '
            f'algorithm=MD5, qop=auth, nc=00000001, cnonce="{cnonce}"')
        if 'opaque' in params:
            request.headers['authorization'] += f', opaque="{params["opaque"]}"'
        phone.sequence += 1
        request.headers['cseq'] = f'{phone.sequence} {request.method}'
        request.headers['via'] = f'SIP/2.0/UDP 127.0.0.1:{phone.port};branch=z9hG4bK{uuid.uuid4().hex};rport'
        phone.send(request)
        return request

    def register(self, phone, expires=120):
        request = phone.request('REGISTER', phone.name)
        request.headers['expires'] = str(expires)
        self.authenticate(phone, request)
        assert self.final(phone, request).status == 200

    def invite(self, line):
        request = self.caller.request('INVITE', '600')
        request.headers['from'] = f'<sip:+15551234567@127.0.0.1>;tag={self.caller.tag}'
        request.headers.update({'x-vowin-line': line, 'content-type': 'application/sdp'})
        request.body = self.sdp()
        return self.authenticate(self.caller, request)

    @staticmethod
    def sdp(port=30000):
        return ('v=0\r\no=test 1 1 IN IP4 127.0.0.1\r\ns=synthetic\r\n'
                f'c=IN IP4 127.0.0.1\r\nt=0 0\r\nm=audio {port} RTP/AVP 8\r\n'
                'a=rtpmap:8 PCMA/8000\r\na=sendrecv\r\n')

    def ack(self, phone, invite, response, success=True):
        uri = (re.search(r'<([^>]+)>', response.headers['contact']).group(1)
               if success else invite.start.split(' ')[1])
        headers = {key: invite.headers[key] for key in ('via', 'from', 'call-id', 'max-forwards')}
        headers['to'] = response.headers['to']
        headers['cseq'] = invite.headers['cseq'].split(' ')[0] + ' ACK'
        if success:
            headers['via'] = f'SIP/2.0/UDP 127.0.0.1:{phone.port};branch=z9hG4bK{uuid.uuid4().hex};rport'
        phone.send(Sip('ACK ' + uri + ' SIP/2.0', headers))

    def ring_and_answer(self, line, winner, participants):
        incoming = self.invite(line)
        branches = [(phone, self.receive(phone, lambda m: m.method == 'INVITE')) for phone in participants]
        assert all('<sip:+15551234567@' in invite.headers['from'] for _, invite in branches), \
            'The PBX replaced the gateway caller number with anonymous'
        for phone, invite in branches:
            phone.respond(invite, 180, 'Ringing')
        assert not any(m.status == 200 and m.headers['call-id'] == incoming.headers['call-id']
                       for m in self.caller.inbox), 'Carrier answered before any phone accepted'
        answered_invite = next(invite for phone, invite in branches if phone is winner)
        accepted = winner.respond(answered_invite, 200, 'OK', self.sdp())
        self.receive(winner, lambda m: m.method == 'ACK' and
                     m.headers['call-id'] == answered_invite.headers['call-id'])
        for phone, invite in branches:
            if phone is winner:
                continue
            cancel = self.receive(phone, lambda m: m.method == 'CANCEL' and
                                  m.headers['call-id'] == invite.headers['call-id'])
            phone.respond(cancel, 200, 'OK')
            phone.respond(invite, 487, 'Request Terminated')
            self.receive(phone, lambda m: m.method == 'ACK' and
                         m.headers['call-id'] == invite.headers['call-id'])
        answer = self.final(self.caller, incoming)
        assert answer.status == 200
        self.ack(self.caller, incoming, answer)
        bye = winner.request('BYE', 'asterisk')
        bye.headers.update({'from': accepted.headers['to'], 'to': answered_invite.headers['from'],
                            'call-id': answered_invite.headers['call-id']})
        winner.send(bye)
        assert self.final(winner, bye).status == 200
        ended = self.receive(self.caller, lambda m: m.method == 'BYE' and
                             m.headers['call-id'] == incoming.headers['call-id'])
        self.caller.respond(ended, 200, 'OK')
        assert not any(m.method == 'INVITE' for phone in self.phones if phone not in participants
                       for m in phone.inbox), 'An unregistered phone was unexpectedly called'
        print(f'PASS {line}: {len(branches)} contacts rang; first answer cancelled every loser; BYE completed.', flush=True)

    def extension_identity(self, caller, callee):
        incoming = caller.request('INVITE', callee.name)
        incoming.headers['content-type'] = 'application/sdp'
        incoming.body = self.sdp()
        self.authenticate(caller, incoming)
        branch = self.receive(callee, lambda m: m.method == 'INVITE')
        assert re.search(r'<sip:' + re.escape(caller.name) + r'@', branch.headers['from']), \
            f'Extension {caller.name} became anonymous: {branch.headers["from"]}'
        callee.respond(branch, 180, 'Ringing')
        cancel = Sip(incoming.start.replace('INVITE ', 'CANCEL ', 1), dict(incoming.headers))
        cancel.headers['cseq'] = incoming.headers['cseq'].split(' ')[0] + ' CANCEL'
        cancel.body = ''
        cancel.headers.pop('content-type', None)
        caller.send(cancel)
        cancelled = self.receive(callee, lambda m: m.method == 'CANCEL' and
                                 m.headers['call-id'] == branch.headers['call-id'])
        callee.respond(cancelled, 200, 'OK')
        callee.respond(branch, 487, 'Request Terminated')
        assert self.final(caller, cancel).status == 200
        final = self.final(caller, incoming)
        assert final.status == 487
        self.ack(caller, incoming, final, success=False)
        self.receive(callee, lambda m: m.method == 'ACK' and m.headers['call-id'] == branch.headers['call-id'])
        print(f'PASS internal {caller.name} -> {callee.name}: caller extension preserved; CANCEL completed.', flush=True)

    def internal_media_and_hangup(self, caller_hangs_up):
        # The client deliberately uses an obsolete URI host but sends via its
        # fixed outbound proxy. The PBX must use the registered user contacts.
        caller, winner = self.two, self.extra
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as caller_rtp, \
                socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as callee_rtp:
            for media in (caller_rtp, callee_rtp):
                media.bind(('127.0.0.1', 0))
                media.setblocking(False)
            incoming = caller.request('INVITE', '1001', domain='old-address.invalid')
            incoming.headers['content-type'] = 'application/sdp'
            incoming.body = self.sdp(caller_rtp.getsockname()[1])
            self.authenticate(caller, incoming)
            branches = [(phone, self.receive(phone, lambda m: m.method == 'INVITE'))
                        for phone in (self.one, self.extra)]
            for phone, branch in branches:
                phone.respond(branch, 180, 'Ringing')
            branch = next(invite for phone, invite in branches if phone is winner)
            accepted = winner.respond(branch, 200, 'OK', self.sdp(callee_rtp.getsockname()[1]))
            self.receive(winner, lambda m: m.method == 'ACK' and m.headers['call-id'] == branch.headers['call-id'])
            loser = next(invite for phone, invite in branches if phone is self.one)
            cancelled = self.receive(self.one, lambda m: m.method == 'CANCEL' and m.headers['call-id'] == loser.headers['call-id'])
            self.one.respond(cancelled, 200, 'OK')
            self.one.respond(loser, 487, 'Request Terminated')
            self.receive(self.one, lambda m: m.method == 'ACK' and m.headers['call-id'] == loser.headers['call-id'])
            answer = self.final(caller, incoming)
            assert answer.status == 200
            self.ack(caller, incoming, answer)

            def media_target(message):
                address = re.search(r'(?m)^c=IN IP4 ([^\r\n]+)', message.body).group(1)
                port = int(re.search(r'm=audio (\d+)', message.body).group(1))
                assert address == '127.0.0.1' and 10000 <= port <= 10099
                return address, port

            targets = (media_target(answer), media_target(branch))
            received = set()
            for sequence in range(120):
                for index, media in enumerate((caller_rtp, callee_rtp)):
                    packet = struct.pack('!BBHII', 0x80, 8, sequence, sequence * 160, index + 1)
                    media.sendto(packet + bytes([0xFA if index == 0 else 0xE8]) * 160, targets[index])
                ready, _, _ = select.select([caller_rtp, callee_rtp], [], [], .02)
                for media in ready:
                    packet, source = media.recvfrom(2048)
                    assert source[0] == '127.0.0.1' and 10000 <= source[1] <= 10099
                    expected = 0xE8 if media is caller_rtp else 0xFA
                    if len(packet) >= 172 and packet[1] & 127 == 8 and bytes([expected]) * 160 in packet[12:]:
                        received.add(media)
                if len(received) == 2:
                    break
            assert len(received) == 2, 'The isolated PBX did not relay both RTP directions'

            if caller_hangs_up:
                origin, other, own_invite = caller, winner, incoming
                local_from, remote_to = incoming.headers['from'], answer.headers['to']
                opposite_id = branch.headers['call-id']
            else:
                origin, other, own_invite = winner, caller, branch
                local_from, remote_to = accepted.headers['to'], branch.headers['from']
                opposite_id = incoming.headers['call-id']
            bye = origin.request('BYE', other.name, domain='another-old-address.invalid')
            bye.headers.update({'from': local_from, 'to': remote_to, 'call-id': own_invite.headers['call-id']})
            origin.send(bye)
            assert self.final(origin, bye).status == 200
            forwarded = self.receive(other, lambda m: m.method == 'BYE' and m.headers['call-id'] == opposite_id)
            if not caller_hangs_up:
                # Drop one BYE response: UDP retransmission must still use the
                # same transaction and not create a new call or carrier leg.
                duplicate = self.receive(other, lambda m: m.method == 'BYE' and m.headers['call-id'] == opposite_id)
                assert duplicate.headers['cseq'] == forwarded.headers['cseq']
                assert duplicate.headers['via'] == forwarded.headers['via']
            other.respond(forwarded, 200, 'OK')
            direction = 'caller' if caller_hangs_up else 'callee (with dropped BYE response)'
            print(f'PASS internal cross-domain: all AOR contacts rang, rewritten Contact and two-way RTP worked; {direction} hangup reached peer.', flush=True)

    def outbound_to_gateway(self, phone, number, expected_number, expected_line, rejected=486):
        incoming = phone.request('INVITE', number)
        incoming.headers['content-type'] = 'application/sdp'
        incoming.body = self.sdp()
        self.authenticate(phone, incoming)
        branch = self.receive(self.caller, lambda m: m.method == 'INVITE')
        assert branch.start.split(' ')[1] == f'sip:{expected_number}@127.0.0.1:5062', branch.start
        assert branch.headers.get('x-vowin-line') == expected_line, 'Unexpected SIM selection'
        # A simulated busy/offline gateway, never a carrier. Failure must not try another SIM.
        self.caller.respond(branch, rejected, 'Busy Here' if rejected == 486 else 'Service Unavailable')
        self.receive(self.caller, lambda m: m.method == 'ACK' and m.headers['call-id'] == branch.headers['call-id'])
        final = self.final(phone, incoming)
        assert 400 <= final.status <= 699 and final.status != 401
        self.ack(phone, incoming, final, success=False)
        assert self.receive(self.caller, lambda m: m.method == 'INVITE', seconds=.2, required=False) is None, \
            'A failed call unexpectedly retried or switched SIM'
        print(f'PASS outbound {phone.name}: {number} -> {expected_number}, line={expected_line}; failure ends without SIM fallback.', flush=True)

    def reject_unsupported_number(self, phone, number):
        incoming = phone.request('INVITE', number)
        incoming.headers['content-type'] = 'application/sdp'
        incoming.body = self.sdp()
        self.authenticate(phone, incoming)
        final = self.final(phone, incoming)
        assert 400 <= final.status <= 699 and final.status != 401
        self.ack(phone, incoming, final, success=False)
        assert self.receive(self.caller, lambda m: m.method == 'INVITE', seconds=.1, required=False) is None, \
            'An unsupported number escaped to the SIM gateway'
        print(f'PASS unsupported {number}: rejected before gateway; no catch-all outbound route.', flush=True)

    def run(self):
        try:
            self.mt_readiness()
            self.register(self.one)
            self.register(self.two)
            self.extension_identity(self.one, self.two)
            self.extension_identity(self.two, self.one)
            self.outbound_to_gateway(self.one, '13800000000', '+8613800000000', 'com6')
            self.outbound_to_gateway(self.two, '14700000000', '+8614700000000', 'com6')
            self.outbound_to_gateway(self.one, '19900000000', '+8619900000000', 'com6', rejected=503)
            self.outbound_to_gateway(self.one, '818613800000000', '+8613800000000', 'com6')
            self.outbound_to_gateway(self.two, '82639171234567', '+639171234567', 'com10')
            for number in ('12800000000', '1380000000', '138000000000', '+639171234567', '01012345678'):
                self.reject_unsupported_number(self.one, number)
            self.ring_and_answer('com6', self.one, [self.one, self.two])
            self.ring_and_answer('com10', self.two, [self.one, self.two])
            # Deliberately unroutable advertised Contact; only observed source
            # routing through rewrite_contact can reach this synthetic phone.
            self.extra.contact_host = '192.0.2.123'
            self.register(self.extra)
            self.internal_media_and_hangup(caller_hangs_up=True)
            self.internal_media_and_hangup(caller_hangs_up=False)
            self.messages()
            self.ring_and_answer('com6', self.two, [self.one, self.two, self.extra])
            self.register(self.two, 0)
            self.ring_and_answer('com10', self.one, [self.one, self.extra])
            self.register(self.extra, 0)
            self.ring_and_answer('com6', self.one, [self.one])
            self.register(self.one, 0)
            for line in ('com6', 'com10', 'unconfigured'):
                invite = self.invite(line)
                final = self.final(self.caller, invite)
                assert 400 <= final.status <= 699 and final.status != 401
                self.ack(self.caller, invite, final, success=False)
                print(f'PASS {line}: no eligible contact or unknown line rejects without answering.', flush=True)
        finally:
            for phone in self.phones:
                phone.socket.close()

    def mt_readiness(self):
        request = mt_readiness_probe(self.caller.port)
        self.caller.send(request)
        response = self.final(self.caller, request)
        realm = algorithm = None
        acceptable = response.status == 200
        if response.status in (401, 407):
            header = 'www-authenticate' if response.status == 401 else 'proxy-authenticate'
            parameters = {key.lower(): quoted or bare for key, quoted, bare in re.findall(
                r'(\w+)=(?:"([^"]*)"|([^,\s]+))', response.headers.get(header, ''))}
            realm, algorithm = parameters.get('realm'), parameters.get('algorithm', 'MD5').upper()
            # The existing local PBX fixture deliberately uses MD5 and the
            # exact realm 'asterisk'; this corresponds to explicit pinned MD5,
            # not the application's SHA-256-only default or realm learning.
            acceptable = realm == 'asterisk' and algorithm == 'MD5' and parameters.get('nonce') and \
                'auth' in parameters.get('qop', '').split(',')
        print('OBSERVED MT readiness OPTIONS: ' + json.dumps({'status': response.status,
            'request_uri': request.start.split()[1], 'realm': realm, 'algorithm': algorithm,
            'body_bytes': len(request.body.encode()), 'authorization_sent': False}), flush=True)
        assert acceptable, f'MT readiness probe is not accepted by the worker: SIP {response.status}'
        print('PASS MT readiness OPTIONS keepalive target is reachable with no SMS payload or Authorization; pinned challenge accepted without sending credentials.', flush=True)

    def messages(self):
        def send_message(phone, target, body, line=None, domain='127.0.0.1', to_user=None, display=None):
            message = phone.request('MESSAGE', target, domain=domain)
            if to_user is not None or display is not None:
                message.headers['to'] = (('"' + display + '" ') if display is not None else '') + f'<sip:{to_user if to_user is not None else target}@{domain}>'
            message.headers['content-type'] = 'text/plain;charset=UTF-8'
            if line:
                message.headers['x-vowin-line'] = line
            message.body = body
            self.authenticate(phone, message)
            assert self.final(phone, message).status == 202  # Accepted by PBX, not SMS delivery.

        for caller, target, recipients in ((self.one, '1002', [self.two]), (self.two, '1001', [self.one, self.extra])):
            send_message(caller, target, 'Local UTF8 文本 🌍', domain='old-message-address.invalid')
            for recipient in recipients:
                received = self.receive(recipient, lambda m: m.method == 'MESSAGE')
                assert received.body == 'Local UTF8 文本 🌍'
                assert f'sip:{caller.name}@' in received.headers['from']
                assert re.search(r'sip:' + caller.name + r'@127\.0\.0\.1(?::5060)?>', received.headers['from'])
                assert re.search(r'sip:' + recipient.name + r'@127\.0\.0\.1(?::5060)?>', received.headers['to'])
                recipient.respond(received, 200, 'OK')
            print('PASS MESSAGE extension routing ignores old URI host after fixed-proxy ingress, reaches all contacts and preserves UTF8/caller.', flush=True)
        for line, number, expected, origin in (('com6', '13800000000', '+8613800000000', self.one),
                                                ('com10', '82639171234567', '+639171234567', self.two)):
            send_message(origin, number, 'Synthetic SMS outbound', line='untrusted-client-line')
            received = self.receive(self.caller, lambda m: m.method == 'MESSAGE')
            assert received.start.split()[1].startswith('sip:' + expected + '@')
            assert received.headers['x-vowin-line'] == line
            assert f'sip:{origin.name}@' in received.headers['from']
            self.caller.respond(received, 202, 'Accepted')
            print('PASS MESSAGE outbound number normalization and fixed line; client line header cannot override route.', flush=True)
        send_message(self.caller, '600', 'Synthetic incoming SMS 收件', line='com6')
        for recipient in (self.one, self.two, self.extra):
            received = self.receive(recipient, lambda m: m.method == 'MESSAGE')
            assert received.body == 'Synthetic incoming SMS 收件'
            assert received.headers['x-vowin-line'] == 'com6'
            recipient.respond(received, 200, 'OK')
        print('PASS MESSAGE incoming group reaches all contacts and preserves modem identity.', flush=True)
        encoded_binding = base64.urlsafe_b64encode(bytes.fromhex('a' * 64)).decode().rstrip('=')
        alias = 'sms2-012345abcdef-' + encoded_binding + '-+15551234567'
        incoming = self.caller.request('MESSAGE', '600')
        incoming.headers.update({'content-type': 'text/plain;charset=UTF-8', 'x-vowin-line': 'com6',
                                 'from': f'"+15551234567" <sip:{alias}@unresolvable.invalid>;tag={self.caller.tag}'})
        incoming.body = 'Synthetic original SIM reply address'
        self.authenticate(self.caller, incoming)
        assert self.final(self.caller, incoming).status == 202
        for recipient in (self.one, self.two, self.extra):
            delivered = self.receive(recipient, lambda m: m.method == 'MESSAGE')
            assert re.search(r'sip:' + re.escape(alias) + r'@127\.0\.0\.1(?::5060)?>', delivered.headers['from'])
            assert '+15551234567' in delivered.headers['from'].split('<')[0]
            assert re.search(r'sip:' + recipient.name + r'@127\.0\.0\.1(?::5060)?>', delivered.headers['to'])
            assert 'unresolvable.invalid' not in delivered.headers['from']
            recipient.respond(delivered, 200, 'OK')
        print('PASS MESSAGE reply host is routable, original SIM alias/display preserved, and each To names the actual recipient.', flush=True)
        send_message(self.one, alias, 'Reply keeps opaque original SIM route')
        reply = self.receive(self.caller, lambda m: m.method == 'MESSAGE')
        assert ('sip:' + alias + '@') in reply.start
        assert 'x-vowin-line' not in reply.headers
        self.caller.respond(reply, 202, 'Accepted')
        print('PASS MESSAGE reply alias reaches gateway unchanged; original SIM resolved there, not by default PBX line.', flush=True)
        for number in ('1234567', '+123456789012345'):
            bounded_alias = 'sms2-012345abcdef-' + encoded_binding + '-' + number
            assert len(bounded_alias) <= 78
            if number.startswith('+'):
                assert len(bounded_alias) == 78
            send_message(self.one, bounded_alias, 'Bounded alias ' + number, display='A safe, named recipient')
            reply = self.receive(self.caller, lambda m: m.method == 'MESSAGE')
            assert reply.start.split()[1] == 'sip:' + bounded_alias + '@127.0.0.1:5062'
            assert 'x-vowin-line' not in reply.headers
            self.caller.respond(reply, 202, 'Accepted')
            print(f'PASS MESSAGE {len(bounded_alias)}-character canonical alias survives Request-URI routing unchanged, including a named To.', flush=True)
        legacy = 'sms-012345abcdef-+15551234567'
        send_message(self.one, legacy, 'Legacy is read-only at gateway')
        reply = self.receive(self.caller, lambda m: m.method == 'MESSAGE')
        assert reply.start.split()[1] == 'sip:' + legacy + '@127.0.0.1:5062'
        assert 'x-vowin-line' not in reply.headers
        self.caller.respond(reply, 403, 'Forbidden')
        assert self.receive(self.caller, lambda m: m.method == 'MESSAGE', seconds=.2, required=False) is None
        print('PASS MESSAGE legacy alias forwarded unchanged for gateway read-only rejection; no numeric/default-line fallback.', flush=True)
        rejected = [
            ('old-long-binding', 'sms2-012345abcdef-' + ('a' * 64) + '-+15551234567', None),
            ('readonly', 'sms-ro-' + ('c' * 64), None),
            ('too-long-number', 'sms2-012345abcdef-' + encoded_binding + '-+1234567890123456', None),
            ('noncanonical-binding', 'sms2-012345abcdef-' + encoded_binding[:-1] + 'p-+15551234567', None),
            ('alias-to-alias-conflict', alias, alias[:-1] + '8'),
            ('numeric-to-alias-conflict', '13800000000', alias),
            ('alias-to-numeric-conflict', alias, '13800000000'),
            ('numeric-to-numeric-conflict', '13800000000', '13900000000'),
            ('local-to-local-conflict', '1002', '1001'),
        ]
        for label, request_user, to_user in rejected:
            send_message(self.one, request_user, 'Must not route ' + label,
                         line='untrusted-client-line', to_user=to_user)
            for endpoint in (self.caller, self.one, self.two, self.extra):
                assert self.receive(endpoint, lambda m: m.method == 'MESSAGE', seconds=.12, required=False) is None, \
                    'Rejected MESSAGE escaped to a gateway or local contact: ' + label
            print('PASS MESSAGE ' + label + ': rejected before dispatch, no paid-number/default-line fallback.', flush=True)
        send_message(self.caller, '600', 'Must not forward', line='unconfigured')
        for recipient in (self.one, self.two, self.extra):
            assert self.receive(recipient, lambda m: m.method == 'MESSAGE', seconds=.1, required=False) is None
        print('PASS MESSAGE unknown carrier line does not forward or loop to SIM.', flush=True)


def isolated():
    if sys.platform != 'linux' or os.geteuid() != 0:
        raise SystemExit('--isolated requires the Linux Docker host with nsenter; never uses production PBX.')
    command('docker', 'image', 'inspect', 'vowin/asterisk:local')
    with tempfile.TemporaryDirectory(prefix='vowin-ring-isolated-') as temporary:
        directory = Path(temporary)
        config = generate(directory, messages=True)
        container = 'vowin-ring-test-' + uuid.uuid4().hex
        args = ['docker', 'run', '-d', '--pull=never', '--name', container, '--network=none',
                '--read-only', '--user=10001:10001', '--cap-drop=ALL', '--security-opt=no-new-privileges:true',
                '--mount', f'type=bind,source={config},target=/etc/asterisk,readonly']
        for path in ('/run/asterisk', '/var/lib/asterisk', '/var/log/asterisk', '/var/spool/asterisk', '/tmp'):
            args += ['--tmpfs', path + ':uid=10001,gid=10001,mode=0750']
        try:
            command(*args, 'vowin/asterisk:local', '-f', '-g', '-vv')
            for _ in range(30):
                try:
                    status = command('docker', 'exec', container, 'asterisk', '-rx', 'module show like chan_pjsip.so')
                except subprocess.CalledProcessError:
                    status = ''
                if 'Running' in status:
                    break
                time.sleep(.2)
            else:
                raise AssertionError('Isolated Asterisk did not start')
            network = json.loads(command('docker', 'inspect', container))[0]
            assert network['HostConfig']['NetworkMode'] == 'none'
            assert not network['HostConfig']['PortBindings']
            pid = network['State']['Pid']
            # Python stays on the host filesystem, but all sockets are confined
            # to the container's loopback-only network namespace.
            try:
                result = command('nsenter', '-t', str(pid), '-n', sys.executable,
                                 str(Path(__file__).resolve()), '--exercise', str(directory / 'credentials.json'), container)
            except subprocess.CalledProcessError as error:
                print(error.output, end='', flush=True)
                print(command('docker', 'logs', container), flush=True)
                raise
            print(result, end='')
            for _ in range(30):
                counts = command('docker', 'exec', container, 'asterisk', '-rx', 'core show channels count')
                if '0 active channels' in counts and '0 active calls' in counts:
                    break
                time.sleep(.1)
            else:
                raise AssertionError('Isolated PBX still has active channels')
            print('PASS isolated PBX has zero remaining channels; production PBX/VoWin/phones were not contacted.')
        finally:
            subprocess.run(['docker', 'rm', '-f', container], check=False, capture_output=True)


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == '--isolated':
        isolated()
    elif len(sys.argv) > 1 and sys.argv[1] == '--exercise':
        IsolatedExercise(json.loads(Path(sys.argv[2]).read_text()), sys.argv[3]).run()
    else:
        unittest.main()
