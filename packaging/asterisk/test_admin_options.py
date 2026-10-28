"""Offline permission tests for the optional PBX administration UI generator."""
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import configure


def deployment():
    return dict(ServerIp='127.0.0.1', WindowsIp='127.0.0.1', Realm='synthetic',
                TrunkUsername='gateway', IncomingTarget='600', Extensions=['1001', '1002'],
                MessagesEnabled=True, Lines=[{'Id': 'synthetic-a'}],
                DialRules=[dict(Pattern='_81X.', Strip=2, Prepend='+', LineId='synthetic-a')])


class AdminOptionsTests(unittest.TestCase):
    def test_internal_only_account_cannot_dial_out_or_send_messages(self):
        values = deployment()
        values['ExtensionOptions'] = {'1001': dict(Name='研究分机', Enabled=True, Ring=False,
            MaxContacts=2, AllowOutgoing=False, AllowMessages=False)}
        with tempfile.TemporaryDirectory() as name, patch.object(os, 'chown', create=True):
            base = Path(name)
            configure.generate(values, base)
            sip = (base/'config/pjsip.conf').read_text(encoding='utf-8')
            dial = (base/'config/extensions.conf').read_text(encoding='utf-8')
            self.assertIn('callerid=研究分机 <1001>', sip)
            self.assertIn('context=from-internal-phones', sip)
            self.assertIn('max_contacts=2', sip)
            incoming = dial.split('[from-vowin]', 1)[1].split('[from-phones]', 1)[0]
            self.assertNotIn('PJSIP_DIAL_CONTACTS(1001)', incoming)
            self.assertIn('PJSIP_DIAL_CONTACTS(1002)', incoming)
            internal = dial.split('[from-internal-phones]', 1)[1].split('[message-deliver]', 1)[0]
            self.assertNotIn('@vowin', internal)
            self.assertNotIn('_81X.', internal)
            self.assertIn('[phone-messages-1001]\nexten => _.,1,Hangup()', dial)
            inbound_sms = dial.split('[from-vowin-messages]', 1)[1]
            self.assertNotIn('Gosub(message-deliver,s,1(1001))', inbound_sms)

    def test_disabled_account_removed_from_registration_and_blocks_exact_route(self):
        values = deployment()
        values['ExtensionOptions'] = {'1001': dict(Enabled=False)}
        with tempfile.TemporaryDirectory() as name, patch.object(os, 'chown', create=True):
            base = Path(name)
            configure.generate(values, base)
            sip = (base/'config/pjsip.conf').read_text()
            dial = (base/'config/extensions.conf').read_text()
            self.assertNotIn('[1001]', sip)
            self.assertNotIn('[1001-auth]', sip)
            self.assertIn('exten => 1001,1,Hangup(21)', dial)
            self.assertIn('1001', json.loads((base/'credentials.json').read_text()))

    def test_unsafe_option_rejected_without_writes(self):
        for option in [dict(Name='a\npassword=evil'), dict(Name='${SHELL(id)}'), dict(MaxContacts=100),
                       dict(Enabled=1), dict(AllowMessages='yes'), dict(Arbitrary='config')]:
            with self.subTest(option=option), tempfile.TemporaryDirectory() as name:
                values = deployment()
                values['ExtensionOptions'] = {'1001': option}
                with self.assertRaises(ValueError):
                    configure.generate(values, Path(name)/'candidate')
                self.assertFalse((Path(name)/'candidate').exists())


if __name__ == '__main__':
    unittest.main()
