"""Send ONE authorized live MESSAGE without REGISTER or changing handset contacts.

Reads deployment/credentials locally. Never prints credentials or message content.
No automatic retry after an uncertain result; --allow-real-sms is required for
destinations other than explicitly configured local extensions.
"""
import argparse
import hashlib
import ipaddress
import json
from pathlib import Path
import re
import socket
import time
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True, type=Path)
    parser.add_argument('--credentials', required=True, type=Path)
    parser.add_argument('--account', required=True)
    parser.add_argument('--destination', required=True)
    parser.add_argument('--text', required=True)
    parser.add_argument('--allow-real-sms', action='store_true')
    args = parser.parse_args()
    config = json.loads(args.config.read_text())
    accounts = config['Extensions']
    if args.account not in accounts:
        parser.error('Account must be an explicitly configured local extension')
    if not re.fullmatch(r'[A-Za-z0-9+_.-]{1,80}', args.destination):
        parser.error('Invalid destination')
    if args.destination not in accounts and not args.allow_real_sms:
        parser.error('Nonlocal destination requires explicit --allow-real-sms')
    body = args.text.encode('utf-8')
    if not body or len(body) > 140 or any(c in args.text for c in '\r\n\0'):
        parser.error('Probe text must be one line, 1..140 UTF-8 bytes')
    password = json.loads(args.credentials.read_text())[args.account]
    address = ipaddress.ip_address(config['ServerIp'])
    host = f'[{address}]' if address.version == 6 else str(address)
    server = (str(address), config.get('SipPort', 5060))
    uri = f'sip:{args.destination}@{host}:{server[1]}'
    started = time.monotonic()
    call_id = f'vowin-message-probe-{uuid.uuid4().hex}'
    with socket.socket(socket.AF_INET6 if address.version == 6 else socket.AF_INET,
                       socket.SOCK_DGRAM) as sock:
        sock.bind((str(address), 0))
        port = sock.getsockname()[1]
        base_headers = {
            'From': f'<sip:{args.account}@{host}>;tag={uuid.uuid4().hex[:12]}',
            'To': f'<{uri}>', 'Call-ID': call_id, 'Max-Forwards': '70',
            'Content-Type': 'text/plain; charset=utf-8',
            'Content-Length': str(len(body)), 'User-Agent': 'VoWin-authorized-message-probe'
        }

        def exchange(sequence, authorization=None):
            headers = dict(base_headers)
            headers['Via'] = f'SIP/2.0/UDP {host}:{port};branch=z9hG4bK{uuid.uuid4().hex};rport'
            headers['CSeq'] = f'{sequence} MESSAGE'
            if authorization:
                headers[authorization[0]] = authorization[1]
            packet = (f'MESSAGE {uri} SIP/2.0\r\n' + ''.join(
                f'{key}: {value}\r\n' for key, value in headers.items()) + '\r\n').encode() + body
            if len(packet) > 1300:
                raise RuntimeError('Probe exceeds conservative UDP MESSAGE limit')
            sock.sendto(packet, server)  # One send; never retry an uncertain submission.
            deadline = time.monotonic() + 12
            while time.monotonic() < deadline:
                sock.settimeout(max(.01, deadline - time.monotonic()))
                raw, remote = sock.recvfrom(65535)
                if remote[:2] != server:
                    continue
                lines = raw.decode('utf-8', errors='replace').split('\r\n\r\n', 1)[0].split('\r\n')
                if not lines[0].startswith('SIP/2.0 '):
                    continue
                received = {k.lower(): v.strip() for k, v in (
                    line.split(':', 1) for line in lines[1:] if ':' in line)}
                if received.get('call-id') != call_id or received.get('cseq') != f'{sequence} MESSAGE':
                    continue
                code = int(lines[0].split()[1])
                if code >= 200:
                    return code, received
            raise TimeoutError('No final response; submission uncertain. Do not automatically retry.')

        code, headers = exchange(1)
        if code in (401, 407):
            params = {key.lower(): quoted or bare for key, quoted, bare in re.findall(
                r'(\w+)=(?:"([^"]*)"|([^,\s]+))',
                headers['www-authenticate' if code == 401 else 'proxy-authenticate'])}
            if params.get('algorithm', 'MD5').upper() != 'MD5' or 'auth' not in params.get('qop', '').split(','):
                raise RuntimeError('Unsupported digest challenge; no credentials transmitted')
            nonce, realm, cnonce = params['nonce'], params['realm'], uuid.uuid4().hex
            md5 = lambda value: hashlib.md5(value.encode()).hexdigest()
            digest = md5(f'{md5(f"{args.account}:{realm}:{password}")}:{nonce}:00000001:{cnonce}:auth:{md5(f"MESSAGE:{uri}")}')
            auth = (f'Digest username="{args.account}", realm="{realm}", nonce="{nonce}", '
                    f'uri="{uri}", response="{digest}", algorithm=MD5, qop=auth, '
                    f'nc=00000001, cnonce="{cnonce}"')
            if 'opaque' in params:
                auth += f', opaque="{params["opaque"]}"'
            code, _ = exchange(2, ('Authorization' if code == 401 else 'Proxy-Authorization', auth))
    print(json.dumps({'callId': call_id, 'sipStatus': code,
                      'elapsedMs': round((time.monotonic() - started) * 1000),
                      'localDestination': args.destination in accounts,
                      'meaning': 'PBX acceptance only; verify VoWin submission history and handset receipt'}))
    return 0 if code in (200, 202) else 1


if __name__ == '__main__':
    raise SystemExit(main())
