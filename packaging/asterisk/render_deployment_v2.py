"""Pure, stopped, secret-bearing deployment bundle. Never applies a service.

Requires Asterisk's explicit per-role digest algorithm support (20.12+, 21.7+
or 22.2+ with PJProject 2.15.1). An older PBX must reject this configuration,
not silently downgrade SHA-256. Runtime capability checking is a separate
admission step. GatewayIp may be an advertised/forwarded address: rendering is
not evidence that its routing, NAT, firewall or device access actually works.
"""
from deployment_v2 import deployment_json_text, normalize_deployment_v2, resolve_deployment_secrets
from render_vocat_v2 import render_vocat_settings
from render_windows_v2 import render_windows_profile


def _host(address):
    return "[" + address + "]" if ":" in address else address


def _endpoint(address, port):
    return _host(address) + ":" + str(port)


def _section(name, fields):
    return "[" + name + "]\n" + "".join(key + "=" + str(value) + "\n" for key, value in fields) + "\n"


def _auth(name, username, password, realm, algorithm):
    return _section(name, [("type", "auth"), ("auth_type", "digest"),
        ("username", username), ("password", password), ("realm", realm),
        ("supported_algorithms_uas", algorithm), ("supported_algorithms_uac", algorithm)])


def _pjsip(model, secrets):
    text = "; Private: explicit role credentials. No algorithm fallback.\n"
    text += _section("global", [("type", "global"), ("endpoint_identifier_order", "ip,username"),
        ("default_realm", model["PbxRealm"])])
    text += _section("transport-udp", [("type", "transport"), ("protocol", "udp"),
        ("bind", _endpoint(model["BindAddress"], model["SipPort"]))])
    common = [("type", "endpoint"), ("transport", "transport-udp"), ("disallow", "all"),
        ("allow", "alaw,ulaw"), ("direct_media", "no"), ("rtp_symmetric", "yes"),
        ("force_rport", "yes"), ("rewrite_contact", "yes"), ("dtmf_mode", "rfc4733")]
    deny = "::/0" if ":" in model["ServerIp"] else "0.0.0.0/0"
    acl = [("deny", deny)] + [("permit", network) for network in model["TrustedNetworks"]]
    peer = model["Peer"]
    if peer["Enabled"]:
        # Direction is relative to the gateway, NOT the PBX.
        outbound, inbound = peer["InboundAuth"], peer["OutboundAuth"]
        text += _auth("gateway-out-auth", outbound["Username"], secrets[outbound["SecretRef"]],
                      model["GatewayRealm"], model["GatewayAlgorithm"])
        text += _auth("gateway-in-auth", inbound["Username"], secrets[inbound["SecretRef"]],
                      model["PbxRealm"], model["PbxAlgorithm"])
        text += _section("gateway-aor", [("type", "aor"),
            ("contact", "sip:gateway@" + _endpoint(model["GatewayIp"], model["GatewayPort"])),
            ("qualify_frequency", 0)])
        text += _section("gateway", common + acl + [("context", "from-gateway"),
            ("message_context", "from-gateway-messages"), ("auth", "gateway-in-auth"),
            ("outbound_auth", "gateway-out-auth"), ("aors", "gateway-aor"), ("identify_by", "ip")])
        bits = "/128" if ":" in model["GatewayIp"] else "/32"
        text += _section("gateway-identify", [("type", "identify"), ("endpoint", "gateway"),
            ("match", _endpoint(model["GatewayIp"], model["GatewayPort"]) + bits)])
    for user in model["Extensions"]:
        option = model["ExtensionOptions"][user]
        if not option["Enabled"]:
            continue
        text += _auth(user + "-auth", user, secrets[option["SecretRef"]], model["PbxRealm"], model["PbxAlgorithm"])
        text += _section(user, common + acl + [("context", "phone-calls-" + user),
            ("message_context", "phone-messages-" + user), ("auth", user + "-auth"),
            ("aors", user), ("identify_by", "username"),
            ("callerid", (option["Name"] or user) + " <" + user + ">")])
        text += _section(user, [("type", "aor"), ("max_contacts", option["MaxContacts"]),
            ("remove_existing", "yes"), ("qualify_frequency", 30)])
    return text


def _route(target, steps):
    return "exten => " + target + ",1," + "\n same => n,".join(steps) + "\n"


def _dial_contacts(users):
    steps = ["Set(VOWIN_RING=)"]
    for user in users:
        steps += ["Set(VOWIN_NEXT=${PJSIP_DIAL_CONTACTS(" + user + ")})",
            'GotoIf($["${VOWIN_NEXT}" = ""]?next_' + user + ')',
            'GotoIf($["${VOWIN_RING}" = ""]?first_' + user + ')',
            "Set(VOWIN_NEXT=${VOWIN_RING}&${VOWIN_NEXT})",
            "(first_" + user + "),Set(VOWIN_RING=${VOWIN_NEXT})",
            "(next_" + user + "),NoOp()"]
    # _route understands Asterisk same=>n(label), rather than n,(label).
    steps += ['GotoIf($["${VOWIN_RING}" = ""]?unavailable)',
        "Dial(${VOWIN_RING},30)", "Hangup()", "(unavailable),Hangup(20)"]
    return steps


def _render_route(target, steps):
    # Labels are internal constants or already validated numeric usernames.
    result = _route(target, steps)
    return result.replace("same => n,(", "same => n(")


def _numeric_guard():
    # Asterisk '.' patterns also match non-digits. Do not let pattern matching
    # alone authorize paid routing, or let a strip/prepend erase bad input.
    return ['Set(VOWIN_NUMERIC=${REGEX("^[+]?[0-9]{1,78}$" ${EXTEN})})',
            'GotoIf($[${VOWIN_NUMERIC} != 1]?reject)']


def _number(rule):
    return rule["Prepend"] + ("${EXTEN:" + str(rule["Strip"]) + "}" if rule["Strip"] else "${EXTEN}")


def _canonical_number(rule, line):
    # Normalize BEFORE adding a Go selector: overlapping prefixes 8/81 must
    # never reinterpret line-8 + a national number starting in 1 as line-81.
    expression = _number(rule)
    steps = ["Set(VOWIN_DEST=" + expression + ")",
        'Set(VOWIN_E164=${REGEX("^[+][1-9][0-9]{6,14}$" ${VOWIN_DEST})})',
        'GotoIf($[${VOWIN_E164} = 1]?number_ok)']
    if line["CountryCode"] and line["NationalNumberLength"]:
        steps += ['Set(VOWIN_NATIONAL=${REGEX("^[0-9]{' + str(line["NationalNumberLength"]) + '}$" ${VOWIN_DEST})})',
            'GotoIf($[${VOWIN_NATIONAL} != 1]?reject)',
            "Set(VOWIN_DEST=+" + line["CountryCode"] + "${VOWIN_DEST})",
            'Set(VOWIN_E164=${REGEX("^[+][1-9][0-9]{6,14}$" ${VOWIN_DEST})})',
            'GotoIf($[${VOWIN_E164} != 1]?reject)']
    else:
        steps += ["Goto(reject)"]
    steps += ["(number_ok),NoOp()"]
    # Retain the transparent expression only when it is already international.
    # Both forms have just been validated and cannot alter the chosen prefix.
    return steps, expression if rule["Prepend"].startswith("+") else "${VOWIN_DEST}"


def _dialplan(model):
    peer = model["Peer"]
    windows = model["GatewayPlatform"] == "vowin-windows"
    enabled = [user for user in model["Extensions"] if model["ExtensionOptions"][user]["Enabled"]]
    ring = [user for user in enabled if model["ExtensionOptions"][user]["Ring"]]
    domain = _endpoint(model["MessageDomain"], model["SipPort"])
    gateway_host = _endpoint(model["GatewayIp"], model["GatewayPort"])
    line_map = {line["Id"]: line for line in model["Lines"]}
    call_from_peer = peer["Enabled"] and model["CallsEnabled"] and model["CarrierEnabled"] and peer["AllowCallsFromPeer"]
    sms_from_peer = peer["Enabled"] and model["SmsEnabled"] and peer["AllowSmsFromPeer"]
    text = "[general]\nstatic=yes\nwriteprotect=yes\n\n"
    # Each authenticated endpoint gets a dedicated context. A spoofed From
    # never changes the principal's carrier/SMS permission or reply identity.
    for user in enabled:
        option = model["ExtensionOptions"][user]
        text += "[phone-calls-" + user + "]\n"
        for target in model["Extensions"]:
            steps = _dial_contacts([target]) if model["CallsEnabled"] and target in enabled else ["Hangup(21)"]
            text += _render_route(target, steps)
        if call_from_peer and option["AllowOutgoing"]:
            for rule in model["DialRules"]:
                line = line_map[rule["LineId"]]
                if not line["AllowOutgoingCalls"] or line["Id"] not in peer["OutgoingLines"]:
                    continue
                number_steps, number = _canonical_number(rule, line)
                destination = number if windows else line["Prefix"] + number
                suffix = ",b(set-gateway-line^s^1(" + line["Id"] + "))" if windows else ""
                steps = _numeric_guard() + number_steps + ["Set(CALLERID(num)=" + user + ")",
                    "Dial(PJSIP/" + destination + "@gateway,60" + suffix + ")",
                    "Hangup()", "(reject),Hangup(21)"]
                text += _render_route(rule["Pattern"], steps)
        text += _route("_!", ["Hangup(21)"]) + "\n"
    if windows and call_from_peer:
        text += "[set-gateway-line]\n" + _route("s", ["Set(PJSIP_HEADER(remove,X-VoWin-Line)=)",
            "Set(PJSIP_HEADER(add,X-VoWin-Line)=${ARG1})", "Return()"]) + "\n"
    text += "[from-gateway]\n"
    if peer["Enabled"] and model["CallsEnabled"] and model["CarrierEnabled"] and peer["AllowCallsToPeer"] and ring:
        targets = sorted({line["CallTarget"] for line in model["Lines"] if line["CallTarget"] and line["AllowIncomingCalls"]})
        for target in targets:
            matching = [line for line in model["Lines"] if line["CallTarget"] == target and line["AllowIncomingCalls"]]
            steps = []
            if windows:
                steps += ["Set(VOWIN_LINE=${PJSIP_HEADER(read,X-VoWin-Line)})"]
                steps += ['GotoIf($["${VOWIN_LINE}" = "' + line["Id"] + '"]?ring)' for line in matching]
                steps += ["Hangup(21)", "(ring),NoOp()"]
            text += _render_route(target, steps + _dial_contacts(ring))
    text += _route("_!", ["Hangup(21)"]) + "\n"
    messaging = model["MessagesEnabled"] or model["SmsEnabled"]
    # Keep disabled contexts rejecting rather than missing/implicit defaults.
    if not messaging:
        for user in enabled:
            text += "[phone-messages-" + user + "]\n" + _route("_!", ["Hangup()"])
        return text + "[from-gateway-messages]\n" + _route("_!", ["Hangup()"])
    text += '''[check-phone-message-target]
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
    # A variable-based parser avoids display-name commas becoming function args.
    text += "[message-deliver]\n" + _render_route("s", [
        "Set(VOWIN_SOURCE_URI=${MESSAGE(from)})",
        "Set(VOWIN_FROM_USER=${PJSIP_PARSE_URI_FROM(VOWIN_SOURCE_URI,user)})",
        'Set(VOWIN_FROM_VALID=${REGEX("^[A-Za-z0-9_+.-]{1,78}$" ${VOWIN_FROM_USER})})',
        'GotoIf($[${VOWIN_FROM_VALID} != 1]?done)',
        "Set(VOWIN_MESSAGE_FROM=sip:${VOWIN_FROM_USER}@" + domain + ")",
        "Set(VOWIN_CONTACTS=${PJSIP_DIAL_CONTACTS(${ARG1})})",
        'While($["${VOWIN_CONTACTS}" != ""])',
        "Set(VOWIN_CONTACT=${SHIFT(VOWIN_CONTACTS,&)})",
        "MessageSend(pjsip:${VOWIN_CONTACT:6},${VOWIN_MESSAGE_FROM},sip:${ARG1}@" + domain + ")",
        "EndWhile()", "(done),Return()"])
    for user in enabled:
        option = model["ExtensionOptions"][user]
        routes = "phone-message-routes-" + user
        text += "\n[phone-messages-" + user + "]\n" + _route("_.", [
            "Set(VOWIN_REQUEST_USER=${EXTEN})", "Gosub(check-phone-message-target,s,1)",
            "Set(MESSAGE(from)=sip:" + user + "@" + domain + ")",
            "Goto(" + routes + ",${EXTEN},1)"])
        text += "[" + routes + "]\n"
        for target in model["Extensions"]:
            allowed = model["MessagesEnabled"] and option["AllowMessages"] and target in enabled and model["ExtensionOptions"][target]["AllowMessages"]
            steps = ["Set(MESSAGE(custom_data)=clear_all_outbound)", "Gosub(message-deliver,s,1(" + target + "))", "Hangup()"] if allowed else ["Hangup()"]
            text += _route(target, steps)
        if sms_from_peer and option["AllowMobileSmsSend"]:
            for pattern, expression in (("_sms-.", "^sms-[0-9a-f]{12}-[+]?[0-9]{7,15}$"),
                ("_sms2-.", "^sms2-[0-9a-f]{12}-[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]-[+]?[0-9]{7,15}$")):
                # Preserve opaque binding and case. The gateway alone checks
                # SIM, selected line and ACL; never prepend a default selector.
                text += _render_route(pattern, ['Set(VOWIN_ALIAS_VALID=${REGEX("' + expression + '" ${EXTEN})})',
                    'GotoIf($[${VOWIN_ALIAS_VALID} != 1]?reject)',
                    "Set(MESSAGE(custom_data)=clear_all_outbound)",
                    "MessageSend(pjsip:PJSIP/${EXTEN}@gateway,${MESSAGE(from)},sip:${EXTEN}@" + gateway_host + ")",
                    "Hangup()", "(reject),Hangup()"])
            for rule in model["SmsDialRules"]:
                line = line_map[rule["LineId"]]
                if not line["AllowSmsSend"] or line["Id"] not in peer["SmsSendLines"]:
                    continue
                number_steps, number = _canonical_number(rule, line)
                destination = number if windows else line["Prefix"] + number
                steps = _numeric_guard() + number_steps + ["Set(MESSAGE(custom_data)=clear_all_outbound)"]
                if windows:
                    steps += ["Set(MESSAGE_DATA(X-VoWin-Line)=" + line["Id"] + ")"]
                steps += ["MessageSend(pjsip:PJSIP/" + destination + "@gateway,${MESSAGE(from)},sip:" + destination + "@" + gateway_host + ")",
                          "Hangup()", "(reject),Hangup()"]
                text += _render_route(rule["Pattern"], steps)
        text += _route("_!", ["Hangup()"])
    text += "\n[from-gateway-messages]\n"
    recipients = [user for user in enabled if model["ExtensionOptions"][user]["AllowMobileSmsReceive"]]
    if peer["Enabled"] and model["SmsEnabled"] and peer["AllowSmsToPeer"] and recipients:
        targets = sorted({line["SmsTarget"] for line in model["Lines"] if line["SmsTarget"] and line["AllowSmsReceive"]})
        for target in targets:
            matching = [line for line in model["Lines"] if line["SmsTarget"] == target and line["AllowSmsReceive"]]
            steps = []
            if windows:
                steps += ["Set(VOWIN_LINE=${MESSAGE_DATA(X-VoWin-Line)})"]
                steps += ['GotoIf($["${VOWIN_LINE}" = "' + line["Id"] + '"]?deliver)' for line in matching]
                steps += ["Hangup()", "(deliver),NoOp()"]
            steps += ["Set(MESSAGE(custom_data)=clear_all_outbound)"]
            steps += ["Gosub(message-deliver,s,1(" + user + "))" for user in recipients]
            text += _render_route(target, steps + ["Hangup()"])
    return text + _route("_!", ["Hangup()"])


def render_deployment_v2(model, secrets_by_ref) -> dict[str, str]:
    """Validate everything before returning eleven new, in-memory text files."""
    model = normalize_deployment_v2(model)
    secrets = resolve_deployment_secrets(model, secrets_by_ref)
    public = deployment_json_text(model)
    gateway = (render_windows_profile(model, secrets) if model["GatewayPlatform"] == "vowin-windows"
               else render_vocat_settings(model, secrets))
    modules = ("res_sorcery_config res_sorcery_memory res_sorcery_astdb res_pjproject res_pjsip "
        "res_pjsip_session res_pjsip_nat res_pjsip_acl res_pjsip_caller_id res_pjsip_pubsub res_pjsip_authenticator_digest "
        "res_pjsip_outbound_authenticator_digest res_pjsip_endpoint_identifier_ip res_pjsip_endpoint_identifier_user "
        "res_pjsip_registrar res_pjsip_sdp_rtp res_pjsip_header_funcs res_pjsip_messaging res_rtp_asterisk "
        "res_timing_timerfd chan_pjsip pbx_config app_dial app_stack app_while func_logic func_strings "
        "bridge_simple bridge_native_rtp codec_alaw codec_ulaw").split()
    firewall = ("# Review only; not installed. No unrequested network access.\n"
        "add table inet vowin_sip_guard\n"
        "add chain inet vowin_sip_guard input { type filter hook input priority -10; policy accept; }\n"
        "flush chain inet vowin_sip_guard input\n")
    family = "ip6" if ":" in model["ServerIp"] else "ip"
    ports = "{ " + str(model["SipPort"]) + ", 10000-10099 }"
    if model["TrustedNetworks"]:
        firewall += "add rule inet vowin_sip_guard input " + family + " saddr { " + ", ".join(model["TrustedNetworks"]) + " } udp dport " + ports + " accept\n"
    firewall += "add rule inet vowin_sip_guard input udp dport " + ports + " drop\n"
    return {
        "config/asterisk.conf": "[directories]\nastetcdir => /etc/asterisk\nastmoddir => /usr/lib/x86_64-linux-gnu/asterisk/modules\nastvarlibdir => /var/lib/asterisk\nastdbdir => /var/lib/asterisk\nastdatadir => /usr/share/asterisk\nastspooldir => /var/spool/asterisk\nastrundir => /run/asterisk\nastlogdir => /var/log/asterisk\n[options]\ndefaultlanguage=en\n",
        "config/modules.conf": "[modules]\nautoload=no\n" + "".join("load=" + name + ".so\n" for name in modules),
        "config/logger.conf": "[general]\n[logfiles]\nconsole => notice,warning,error\n",
        "config/rtp.conf": "[general]\nrtpstart=10000\nrtpend=10099\nicesupport=no\nstrictrtp=yes\n",
        "config/http.conf": "[general]\nenabled=no\n",
        "config/manager.conf": "[general]\nenabled=no\nwebenabled=no\n",
        "config/pjsip.conf": _pjsip(model, secrets),
        "config/extensions.conf": _dialplan(model),
        "gateway-client.json": deployment_json_text(gateway),
        "deployment.normalized.json": public,
        "sip-guard.nft": firewall,
    }


def render_installable_deployment_v2(model, secrets_by_ref) -> dict[str, str]:
    """Complete twenty-file package, retaining the original eleven-file API.

    Asterisk reads several core configuration files even with autoload=no.
    Supply them instead of depending on missing-file fallbacks. None enables
    a new listener, authentication role, recording, event output or modem.
    These are small configuration texts, not bundled runtimes/dependencies.
    """
    result = render_deployment_v2(model, secrets_by_ref)
    result.update({
        "config/stasis.conf": "; Keep default core event processing; decline no message types.\n[declined_message_types]\n",
        "config/udptl.conf": "; No fax module or T.38 endpoint is enabled.\n[general]\nudptlstart=4000\nudptlend=4099\n",
        "config/acl.conf": "; No named ACLs. Every endpoint uses explicit deny/permit CIDRs.\n",
        "config/indications.conf": "; xx is a neutral tone profile, not a SIM country or a dial rule.\n[general]\ncountry=xx\n[xx]\ndescription=VoWin neutral tones\nringcadence=1000,4000\ndial=425\nbusy=425/500,0/500\ncongestion=425/250,0/250\nring=425/1000,0/4000\n",
        "config/features.conf": "; No dynamic feature or recording mappings.\n[general]\n[featuremap]\n[applicationmap]\n",
        "config/cel.conf": "[general]\nenable=no\n[manager]\nenabled=no\n",
        "config/cdr.conf": "[general]\nenable=no\n",
        "config/ccss.conf": "[general]\ncc_max_requests=0\n",
        "config/pjproject.conf": "[startup]\ntype=startup\nlog_level=2\n[log_mappings]\ntype=log_mappings\nasterisk_error=0,1\nasterisk_warning=2\n",
    })
    return result
