"""Generate a private PBX from explicit JSON. No reload, firewall install or network actions.

Addresses, extensions, modem IDs and number routes come from the deployment file.
Existing credentials are preserved and never printed. See deployment.example.json.
"""
import argparse
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets


def identifier(value):
    return isinstance(value, str) and re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,63}', value)


def validate(values):
    if not isinstance(values, dict) or type(values.get('MessagesEnabled', False)) is not bool:
        raise ValueError('Deployment must be an object; MessagesEnabled must be boolean.')
    for field in ('ServerIp', 'WindowsIp'):
        address = ipaddress.IPv4Address(values[field])
        if not address.is_private or address.is_unspecified or address.is_multicast:
            raise ValueError('Explicit private IPv4 address required: ' + field)
    bind = ipaddress.IPv4Address(values.get('BindAddress', values['ServerIp']))
    if not bind.is_unspecified and (not bind.is_private or bind.is_multicast):
        raise ValueError('Invalid BindAddress.')
    for field, default in [('SipPort', 5060), ('WindowsPort', 5062)]:
        if type(values.get(field, default)) is not int or not 1 <= values.get(field, default) <= 65535:
            raise ValueError('Invalid UDP port: ' + field)
    for field in ('TrunkUsername', 'IncomingTarget', 'Realm'):
        if not identifier(values[field]):
            raise ValueError('Invalid identifier: ' + field)
    message_domain = values.get('MessageDomain', values['ServerIp'])
    if not isinstance(message_domain, str) or not re.fullmatch(
            r'(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*', message_domain):
        raise ValueError('MessageDomain must be a reachable SIP hostname or private IPv4 address, without a port.')
    if re.fullmatch(r'[0-9.]+', message_domain):
        address = ipaddress.IPv4Address(message_domain)
        if not address.is_private or address.is_unspecified or address.is_multicast:
            raise ValueError('MessageDomain requires a private IPv4 address or hostname.')
    extensions, lines = values.get('Extensions', []), values.get('Lines', [])
    if not 1 <= len(extensions) <= 64 or any(not identifier(item) or item.startswith('_') for item in extensions):
        raise ValueError('Configure 1 to 64 distinct extension accounts.')
    if len(set(extensions)) != len(extensions) or values['TrunkUsername'] in extensions:
        raise ValueError('Extension and trunk accounts must be distinct.')
    if values['IncomingTarget'] in extensions or values.get('EchoExtension') in extensions:
        raise ValueError('Service targets collide with extension accounts.')
    if values.get('EchoExtension') and not identifier(values['EchoExtension']):
        raise ValueError('Invalid EchoExtension.')
    options = values.get('ExtensionOptions', {})
    if not isinstance(options, dict) or set(options) - set(extensions):
        raise ValueError('ExtensionOptions must reference configured extensions.')
    for option in options.values():
        if not isinstance(option, dict) or set(option) - {'Name', 'Enabled', 'Ring', 'MaxContacts', 'AllowOutgoing', 'AllowMessages'}:
            raise ValueError('Invalid extension options.')
        for key in ('Enabled', 'Ring', 'AllowOutgoing', 'AllowMessages'):
            if key in option and type(option[key]) is not bool:
                raise ValueError('Extension flags must be boolean.')
        name = option.get('Name', '')
        if not isinstance(name, str) or len(name.encode()) > 80 or any(ord(c) < 32 or c in ';<>"\\$' for c in name):
            raise ValueError('Invalid extension display name.')
        if type(option.get('MaxContacts', 4)) is not int or not 1 <= option.get('MaxContacts', 4) <= 8:
            raise ValueError('MaxContacts must be 1 to 8.')
    if len(lines) > 64 or any(not identifier(line['Id']) for line in lines) or len({line['Id'] for line in lines}) != len(lines):
        raise ValueError('Invalid or duplicate modem line IDs.')
    line_ids = {line['Id'] for line in lines}
    rules = values.get('DialRules', [])
    if len(rules) > 64:
        raise ValueError('Too many dial rules.')
    patterns = set()
    for rule in rules:
        pattern = rule['Pattern']
        if not isinstance(pattern, str) or len(pattern) > 64 or not re.fullmatch(r'_(?:[0-9XZN+]|\[[0-9]+(?:-[0-9]+)?\])+\.?', pattern):
            raise ValueError('Invalid numeric dial pattern.')
        if pattern in patterns or pattern in ('_.', '_X.', '_X', '_!'):
            raise ValueError('Duplicate or unrestricted catch-all dial pattern.')
        patterns.add(pattern)
        if rule['LineId'] not in line_ids or type(rule.get('Strip', 0)) is not int or not 0 <= rule.get('Strip', 0) <= 20:
            raise ValueError('Dial rule needs an existing modem and valid Strip count.')
        if not re.fullmatch(r'\+?[0-9]{0,12}', rule.get('Prepend', '')):
            raise ValueError('Invalid number prepend.')
    for network in values.get('TrustedNetworks', []):
        parsed = ipaddress.IPv4Network(network, strict=True)
        if not parsed.is_private or parsed.prefixlen == 0:
            raise ValueError('TrustedNetworks requires private IPv4 CIDRs.')
    if bind.is_unspecified and not values.get('TrustedNetworks'):
        raise ValueError('Wildcard binding needs explicit TrustedNetworks; install firewall before starting PBX.')


def generate(values, base):
    validate(values)
    def option(extension, key, default):
        return values.get('ExtensionOptions', {}).get(extension, {}).get(key, default)

    enabled = [e for e in values['Extensions'] if option(e, 'Enabled', True)]
    ring_targets = [e for e in enabled if option(e, 'Ring', True)]
    message_targets = [e for e in enabled if option(e, 'AllowMessages', True)]
    base.mkdir(parents=True, exist_ok=True)
    credentials = base / 'credentials.json'
    passwords = json.loads(credentials.read_text()) if credentials.exists() else {}
    for name in (values['TrunkUsername'], *values['Extensions']):
        if name not in passwords:
            passwords[name] = secrets.token_hex(24)
        if not re.fullmatch(r'[A-Za-z0-9_-]{16,128}', passwords[name]):
            raise ValueError('Unsupported existing credential format; not overwritten.')
    config = base / 'config'
    config.mkdir(mode=0o750, exist_ok=True)
    if hasattr(os, 'chown') and os.name != 'nt':
        os.chown(config, 10001, 10001)

    def save(path, text, mode=0o640, owner=True):
        temporary = path.with_name(path.name + '.tmp')
        descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, mode)
        try:
            with os.fdopen(descriptor, 'w', encoding='utf-8') as output:
                output.write(text.strip() + '\n')
            temporary.chmod(mode)
            if owner and hasattr(os, 'chown') and os.name != 'nt':
                os.chown(temporary, 10001, 10001)
            temporary.replace(path)
        finally:
            if temporary.exists():
                temporary.unlink()

    save(credentials, json.dumps(passwords), 0o600, False)
    save(config / 'asterisk.conf', '''[directories]
astetcdir => /etc/asterisk
astmoddir => /usr/lib/x86_64-linux-gnu/asterisk/modules
astvarlibdir => /var/lib/asterisk
astdbdir => /var/lib/asterisk
astkeydir => /usr/share/asterisk/keys
astdatadir => /usr/share/asterisk
astagidir => /usr/share/asterisk/agi-bin
astspooldir => /var/spool/asterisk
astrundir => /run/asterisk
astlogdir => /var/log/asterisk
[options]
defaultlanguage = en''')
    modules = '''res_sorcery_config res_sorcery_memory res_sorcery_astdb res_pjproject res_pjsip
res_pjsip_session res_pjsip_nat res_pjsip_caller_id res_pjsip_pubsub res_pjsip_authenticator_digest
res_pjsip_outbound_authenticator_digest res_pjsip_endpoint_identifier_ip res_pjsip_endpoint_identifier_user
res_pjsip_registrar res_pjsip_sdp_rtp res_pjsip_header_funcs res_rtp_asterisk res_timing_timerfd
chan_pjsip pbx_config app_dial app_echo app_playback app_stack func_logic bridge_simple bridge_native_rtp
codec_alaw codec_ulaw format_gsm'''.split()
    if values.get('MessagesEnabled', False):
        modules += ['res_pjsip_messaging', 'app_while', 'func_strings']
    save(config / 'modules.conf', '[modules]\nautoload=no\n' + '\n'.join('load=' + name + '.so' for name in modules))
    save(config / 'logger.conf', '[general]\n[logfiles]\nconsole => notice,warning,error')
    save(config / 'rtp.conf', '[general]\nrtpstart=10000\nrtpend=10099\nicesupport=no\nstrictrtp=yes')
    save(config / 'http.conf', '[general]\nenabled=no')
    save(config / 'manager.conf', '[general]\nenabled=no\nwebenabled=no')
    wp, sp, username = values.get('WindowsPort', 5062), values.get('SipPort', 5060), values['TrunkUsername']
    pjsip = f'''[global]
type=global
endpoint_identifier_order=ip,username
[transport-udp]
type=transport
protocol=udp
bind={values.get('BindAddress', values['ServerIp'])}:{sp}
[vowin-in]
type=auth
auth_type=userpass
username={username}
password={passwords[username]}
realm=asterisk
[vowin-out]
type=auth
auth_type=userpass
username={username}
password={passwords[username]}
realm=*
[vowin-aor]
type=aor
contact=sip:gateway@{values['WindowsIp']}:{wp}
qualify_frequency=0
[vowin]
type=endpoint
transport=transport-udp
context=from-vowin
auth=vowin-in
outbound_auth=vowin-out
aors=vowin-aor
identify_by=ip
disallow=all
allow=alaw,ulaw
direct_media=no
rtp_symmetric=yes
force_rport=yes
rewrite_contact=yes
dtmf_mode=rfc4733
message_context=from-vowin-messages
[vowin-identify]
type=identify
endpoint=vowin
match={values['WindowsIp']}:{wp}/32
[phone](!)
type=endpoint
transport=transport-udp
context=from-phones
disallow=all
allow=alaw,ulaw
direct_media=no
rtp_symmetric=yes
force_rport=yes
rewrite_contact=yes
dtmf_mode=rfc4733
identify_by=username
'''
    for extension in enabled:
        pjsip += f'''\n[{extension}](phone)
context={'from-phones' if option(extension, 'AllowOutgoing', True) else 'from-internal-phones'}
auth={extension}-auth
aors={extension}
callerid={option(extension, 'Name', '') or ('VoWin ' + extension)} <{extension}>
message_context=phone-messages-{extension}
[{extension}-auth]
type=auth
auth_type=userpass
username={extension}
password={passwords[extension]}
[{extension}]
type=aor
max_contacts={option(extension, 'MaxContacts', 4)}
remove_existing=yes
qualify_frequency=30
'''
    save(config / 'pjsip.conf', pjsip)
    checks = ''.join(f' same => n,GotoIf($["${{VOWIN_LINE}}" = "{line["Id"]}"]?ring)\n' for line in values['Lines'])
    ring = ''
    for extension in ring_targets:
        ring += f''' same => n,Set(VOWIN_NEXT=${{PJSIP_DIAL_CONTACTS({extension})}})
 same => n,GotoIf($["${{VOWIN_NEXT}}" = ""]?next_{extension})
 same => n,GotoIf($["${{VOWIN_RING}}" = ""]?first_{extension})
 same => n,Set(VOWIN_NEXT=${{VOWIN_RING}}&${{VOWIN_NEXT}})
 same => n(first_{extension}),Set(VOWIN_RING=${{VOWIN_NEXT}})
 same => n(next_{extension}),NoOp()
'''
    dialplan = f'''[general]
static=yes
writeprotect=yes
[from-vowin]
exten => {values['IncomingTarget']},1,Set(VOWIN_LINE=${{PJSIP_HEADER(read,X-VoWin-Line)}})
{checks} same => n,Hangup(21)
 same => n(ring),Set(VOWIN_RING=)
{ring} same => n,GotoIf($["${{VOWIN_RING}}" = ""]?unavailable)
 same => n,Dial(${{VOWIN_RING}},30)
 same => n,Hangup()
 same => n(unavailable),Hangup(20)
[from-phones]
'''
    internal_dialplan = ''
    for extension in values['Extensions']:
        # Resolve every registered contact, not the arbitrary first AOR entry.
        # A stale LAN contact must not hide the same user's current VPN contact.
        route = f'''exten => {extension},1,Set(VOWIN_CONTACTS=${{PJSIP_DIAL_CONTACTS({extension})}})
 same => n,GotoIf($["${{VOWIN_CONTACTS}}" = ""]?unavailable)
 same => n,Dial(${{VOWIN_CONTACTS}},30)
 same => n,Hangup()
 same => n(unavailable),Hangup(20)
'''
        if extension not in enabled:
            route = f'exten => {extension},1,Hangup(21)\n'
        dialplan += route
        internal_dialplan += route
    if values.get('EchoExtension'):
        dialplan += f'exten => {values["EchoExtension"]},1,Answer()\n same => n,Echo()\n same => n,Hangup()\n'
    for rule in values.get('DialRules', []):
        number = rule.get('Prepend', '') + ('${EXTEN}' if not rule.get('Strip') else '${EXTEN:' + str(rule['Strip']) + '}')
        dialplan += f'exten => {rule["Pattern"]},1,Dial(PJSIP/{number}@vowin,60,b(set-vowin-line^s^1({rule["LineId"]})))\n same => n,Hangup()\n'
    dialplan += '[set-vowin-line]\nexten => s,1,Set(PJSIP_HEADER(add,X-VoWin-Line)=${ARG1})\n same => n,Return()\n'
    dialplan += '[from-internal-phones]\n' + internal_dialplan
    if values.get('MessagesEnabled', False):
        message_domain = values.get('MessageDomain', values['ServerIp'])
        dialplan += f'''[message-deliver]
exten => s,1,Set(VOWIN_FROM_USER=${{PJSIP_PARSE_URI(${{MESSAGE(from)}},user)}})
 same => n,Set(VOWIN_FROM_DISPLAY=${{PJSIP_PARSE_URI(${{MESSAGE(from)}},display)}})
 same => n,GotoIf($["${{VOWIN_FROM_USER}}" = ""]?done)
 same => n,Set(VOWIN_MESSAGE_FROM="${{VOWIN_FROM_DISPLAY}}" <sip:${{VOWIN_FROM_USER}}@{message_domain}:{sp}>)
 same => n,Set(VOWIN_CONTACTS=${{PJSIP_DIAL_CONTACTS(${{ARG1}})}})
'''
        dialplan += f''' same => n,While($["${{VOWIN_CONTACTS}}" != ""])
 same => n,Set(VOWIN_CONTACT=${{SHIFT(VOWIN_CONTACTS,&)}})
 same => n,MessageSend(pjsip:${{VOWIN_CONTACT:6}},${{VOWIN_MESSAGE_FROM}},sip:${{ARG1}}@{message_domain}:{sp})
 same => n,EndWhile()
 same => n(done),Return()
[from-phone-messages]
'''
        # Opaque reply addresses are resolved and SIM-checked by VoWin, never
        # normalized into a phone number or routed through a default SIM here.
        # Legacy addresses are forwarded too, so the gateway can reject them
        # explicitly rather than accidentally matching a paid numeric rule.
        # sms2 carries the full 256-bit SIM digest as canonical, unpadded
        # base64url, so even a +15-digit number fits in 78 characters. Asterisk
        # 22 truncates the Request-URI user to AST_MAX_EXTENSION-1 (79): never
        # recover a long/truncated target from the independently supplied To.
        aliases = (
            ('_sms-.', '^sms-[0-9a-f]{12}-[+]?[0-9]{7,15}$'),
            ('_sms2-.', '^sms2-[0-9a-f]{12}-[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]-[+]?[0-9]{7,15}$'),
        )
        for alias_pattern, alias_regex in aliases:
            dialplan += f'''exten => {alias_pattern},1,Set(VOWIN_ALIAS_VALID=${{REGEX("{alias_regex}" ${{EXTEN}})}})
 same => n,GotoIf($[${{VOWIN_ALIAS_VALID}} = 1]?send)
 same => n,Hangup()
 same => n(send),Set(MESSAGE(custom_data)=clear_all_outbound)
 same => n,MessageSend(pjsip:PJSIP/${{EXTEN}}@vowin,${{MESSAGE(from)}},sip:${{EXTEN}}@{values['WindowsIp']}:{wp})
 same => n,Hangup()
'''
        for extension in values['Extensions']:
            if extension in message_targets:
                dialplan += f'exten => {extension},1,Gosub(message-deliver,s,1({extension}))\n same => n,Hangup()\n'
            else:
                dialplan += f'exten => {extension},1,Hangup()\n'
        for rule in values.get('DialRules', []):
            number = rule.get('Prepend', '') + ('${EXTEN}' if not rule.get('Strip') else '${EXTEN:' + str(rule['Strip']) + '}')
            dialplan += f'''exten => {rule['Pattern']},1,Set(MESSAGE(custom_data)=clear_all_outbound)
 same => n,Set(MESSAGE_DATA(X-VoWin-Line)={rule['LineId']})
 same => n,MessageSend(pjsip:PJSIP/{number}@vowin,${{MESSAGE(from)}},sip:{number}@{values['WindowsIp']}:{wp})
 same => n,Hangup()
'''
        # MESSAGE(to) is the complete To, not the routing Request-URI. It is
        # only a consistency guard. Its tech wrapper is added by Asterisk's
        # res_pjsip_messaging: pj+sip(s): for a bare URI, pjsip:+nameaddr for a
        # display/nameaddr. Parse a stored variable so commas/quotes in display
        # names cannot become function arguments. Do not use MESSAGE_DATA's
        # arbitrary Request-URI header as proof of the actual Request-URI.
        dialplan += '''[check-phone-message-target]
exten => s,1,Set(VOWIN_MESSAGE_TO=${MESSAGE(to)})
 same => n,Set(VOWIN_TO_NAMED=${REGEX("^pjsip:.*<" ${VOWIN_MESSAGE_TO})})
 same => n,GotoIf($[${VOWIN_TO_NAMED} = 1]?named)
 same => n,Set(VOWIN_TO_URI=${VOWIN_MESSAGE_TO:2})
 same => n,Goto(parse)
 same => n(named),Set(VOWIN_TO_URI=${VOWIN_MESSAGE_TO:6})
 same => n(parse),Set(VOWIN_TO_USER=${PJSIP_PARSE_URI_FROM(VOWIN_TO_URI,user)})
 same => n,Set(VOWIN_TO_VALID=${REGEX("^[A-Za-z0-9_+.-]{1,78}$" ${VOWIN_TO_USER})})
 same => n,GotoIf($[${VOWIN_TO_VALID} != 1]?reject)
 same => n,Set(VOWIN_RURI_VALID=${REGEX("^[A-Za-z0-9_+.-]{1,78}$" ${VOWIN_REQUEST_USER})})
 same => n,GotoIf($[${VOWIN_RURI_VALID} != 1]?reject)
 same => n,GotoIf($["${VOWIN_TO_USER}" != "${VOWIN_REQUEST_USER}"]?reject)
 same => n,Return()
 same => n(reject),Hangup()
'''
        # The endpoint's authenticated context sets From, not a spoofable header.
        # Check all phone MESSAGE routes before dispatch: an alias in To must
        # not disguise a numeric Request-URI that would otherwise reach a SIM.
        for extension in values['Extensions']:
            if extension not in message_targets:
                dialplan += f'[phone-messages-{extension}]\nexten => _.,1,Hangup()\n'
                continue
            dialplan += f'''[phone-messages-{extension}]
exten => _.,1,Set(VOWIN_REQUEST_USER=${{EXTEN}})
 same => n,Gosub(check-phone-message-target,s,1)
 same => n,Set(MESSAGE(from)=sip:{extension}@{message_domain}:{sp})
 same => n,Goto(from-phone-messages,${{EXTEN}},1)
'''
        dialplan += '[from-vowin-messages]\n'
        for target in (values['IncomingTarget'], *values['Extensions']):
            dialplan += f'exten => {target},1,Set(VOWIN_LINE=${{MESSAGE_DATA(X-VoWin-Line)}})\n' + checks
            dialplan += ' same => n,Hangup()\n same => n(ring),Set(MESSAGE(custom_data)=clear_all_outbound)\n'
            dialplan += ' same => n,Set(MESSAGE_DATA(X-VoWin-Line)=${VOWIN_LINE})\n'
            for extension in message_targets if target == values['IncomingTarget'] else ([target] if target in message_targets else []):
                dialplan += f' same => n,Gosub(message-deliver,s,1({extension}))\n'
            dialplan += ' same => n,Hangup()\n'
    save(config / 'extensions.conf', dialplan)
    profile = {'Settings': {
        'UseTrunk': True, 'BindAddress': values['WindowsIp'], 'Port': wp, 'Realm': values['Realm'],
        'ServerAddress': values['ServerIp'], 'ServerPort': sp, 'TrunkUsername': username,
        'IncomingTarget': values['IncomingTarget'], 'AllowOutboundCalls': False,
        'MessagesEnabled': values.get('MessagesEnabled', False), 'ForwardIncomingSms': False, 'AllowOutgoingSms': False,
        'LineRoutes': {line['Id']: {'Enabled': True, 'AllowIncomingCalls': True, 'AllowOutgoingCalls': True,
                                  'ForwardIncomingSms': False, 'AllowOutgoingSms': False} for line in values['Lines']}
    }, 'Password': passwords[username]}
    save(base / 'vowin-client.json', json.dumps(profile, indent=2), 0o600, False)
    networks = ', '.join(values.get('TrustedNetworks', []))
    firewall = f'''# Review and install before exposing SIP. No public inbound.
add table inet vowin_sip_guard
add chain inet vowin_sip_guard input {{ type filter hook input priority -10; policy accept; }}
flush chain inet vowin_sip_guard input
add rule inet vowin_sip_guard input iifname "lo" udp dport {{ {sp}, 10000-10099 }} accept
'''
    if networks:
        firewall += f'add rule inet vowin_sip_guard input ip saddr {{ {networks} }} udp dport {{ {sp}, 10000-10099 }} accept\n'
    firewall += f'add rule inet vowin_sip_guard input udp dport {{ {sp}, 10000-10099 }} drop\n'
    save(base / 'vowin-sip-guard.nft', firewall, 0o600, False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True, type=Path)
    parser.add_argument('--secrets', type=Path, help='Version 2 only: explicit private SecretRef map')
    parser.add_argument('--output', type=Path, help='Default: generated/ next to script')
    args = parser.parse_args()
    try:
        with args.config.open('rb') as handle:
            raw = handle.read(65537)
        if not 0 < len(raw) <= 65536:
            raise SystemExit('Deployment JSON must contain 1..65536 UTF-8 bytes.')
        values = json.loads(raw.decode('utf-8', 'strict'))
    except (OSError, UnicodeError, ValueError, RecursionError):
        raise SystemExit('Invalid deployment input; no files were generated.') from None
    is_v2 = isinstance(values, dict) and any(key.casefold() == 'schemaversion' for key in values)
    if is_v2:
        # Lazy imports preserve the independent legacy generator entry point;
        # v2 must never use its username-keyed shared/generated credentials.
        from deployment_v2 import DeploymentV2Error, parse_deployment_v2, parse_deployment_secrets
        from render_deployment_v2 import render_installable_deployment_v2
        from deployment_bundle_v2 import write_installable_deployment_bundle
        try:
            if args.secrets is None:
                raise DeploymentV2Error('Secrets: explicit private role-reference input is required')
            model = parse_deployment_v2(raw)
            with args.secrets.open('rb') as handle:
                private = parse_deployment_secrets(handle.read(65537), model)
            bundle = render_installable_deployment_v2(model, private)
            write_installable_deployment_bundle(bundle, args.output or Path(__file__).resolve().parent / 'generated-v2')
        except (DeploymentV2Error, OSError):
            raise SystemExit('Invalid version 2 deployment or output; no existing deployment was changed.') from None
        print('New private version 2 bundle generated; stopped gateway configuration, no network actions performed.')
        return
    if args.secrets is not None:
        raise SystemExit('Private role-reference input requires version 2; legacy credentials were not changed.')
    try:
        generate(values, args.output or Path(__file__).resolve().parent / 'generated')
    except (KeyError, ValueError, TypeError) as error:
        raise SystemExit('Invalid deployment configuration: ' + str(error)) from None
    print('Private deployment generated; credentials preserved, no network actions performed.')


if __name__ == '__main__':
    main()
