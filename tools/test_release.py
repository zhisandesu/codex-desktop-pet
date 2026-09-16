"""Synthetic secret fixtures are constructed at runtime, never real credentials."""
import unittest
from release import findings, source_allowed, vendor_path_exception, VENDOR_PROFILE_HASHES
import hashlib

class ReleaseGuardTests(unittest.TestCase):
    def test_vendor_exception_is_narrow(self):
        data = b'synthetic vendor bytes'
        VENDOR_PROFILE_HASHES['fixture.dll'] = hashlib.sha256(data).hexdigest()
        try:
            self.assertTrue(vendor_path_exception('fixture.dll', data, 'user-profile-path'))
            self.assertFalse(vendor_path_exception('fixture.dll', data + b'x', 'user-profile-path'))
            self.assertFalse(vendor_path_exception('other.dll', data, 'user-profile-path'))
            self.assertFalse(vendor_path_exception('fixture.dll', data, 'service-token'))
        finally:
            del VENDOR_PROFILE_HASHES['fixture.dll']

    def test_secret_and_utf16(self):
        secret = 's' + 'k-' + 'A1b2C3d4' * 6
        for encoding in ('utf-8', 'utf-16-le'):
            self.assertTrue(list(findings(secret.encode(encoding))))

    def test_uuid_credential(self):
        value = 'api' + '_key = "' + '01234567-89ab-cdef-0123-456789abcdef' + '"'
        self.assertTrue(list(findings(value.encode())))

    def test_identifiers_are_not_keys(self):
        self.assertFalse(list(findings(b'"task-context-feedback-workload"')))
        self.assertFalse(list(findings(b'apiKey = "XIAOBIAN_ARK_API_KEY"')))

    def test_allowlist(self):
        for path in ('XiaobianPet/Services/SpeechService.cs', 'XiaobianPet/Assets/animations/idle-video/00.png'):
            self.assertTrue(source_allowed(path))
        for path in ('.env', 'XiaobianPet/settings.json', 'XiaobianPet/Assets/animations/idle-video/response.json',
                     'XiaobianPet/Assets/animation-v9-preview/result.json', 'XiaobianPet/artifacts/secret.txt'):
            self.assertFalse(source_allowed(path))

if __name__ == '__main__':
    unittest.main()
