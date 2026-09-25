import importlib.util
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("upload_oss", Path(__file__).with_name("upload-oss.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ServerError(Exception):
    status = 409
    code = "FileAlreadyExists"


class UploadTests(unittest.TestCase):
    def test_upload_retry_and_conflict(self):
        with tempfile.TemporaryDirectory() as directory, patch("builtins.print"), patch.dict(os.environ, {
            "OSS_ACCESS_KEY_ID": "test-id", "OSS_ACCESS_KEY_SECRET": "test-secret",
            "GITHUB_OUTPUT": str(Path(directory) / "output"),
        }):
            package = Path(directory) / "CNGoldenLink-0.1.0.zip"
            package.write_bytes(b"test package")
            bucket = Mock()
            sdk = SimpleNamespace(Auth=Mock(), Bucket=Mock(return_value=bucket),
                                  exceptions=SimpleNamespace(ServerError=ServerError))
            url = module.upload(package, sdk)
            self.assertEqual(url, "https://aliyun-static.diving-fish.com/cngist/CNGoldenLink-0.1.0.zip")
            headers = bucket.put_object_from_file.call_args.kwargs["headers"]
            self.assertEqual(headers["x-oss-forbid-overwrite"], "true")
            self.assertNotIn("test-secret", str(headers))
            bucket.put_object_from_file.side_effect = ServerError()
            bucket.head_object.return_value = SimpleNamespace(
                headers={"x-oss-meta-sha256": headers["x-oss-meta-sha256"]},
                content_length=package.stat().st_size)
            self.assertEqual(module.upload(package, sdk), url)
            bucket.head_object.return_value.headers = {}
            with self.assertRaisesRegex(ValueError, "different package"):
                module.upload(package, sdk)
            output = Path(os.environ["GITHUB_OUTPUT"]).read_text()
            self.assertNotIn("test-secret", output)

    def test_latest_manifest(self):
        with patch("builtins.print"), patch.dict(os.environ, {
            "OSS_ACCESS_KEY_ID": "test-id", "OSS_ACCESS_KEY_SECRET": "test-secret"}):
            bucket = Mock()
            bucket.head_object.return_value = SimpleNamespace(headers={"x-oss-meta-sha256": "abc"})
            sdk = SimpleNamespace(Auth=Mock(), Bucket=Mock(return_value=bucket))
            manifest = module.publish_latest("0.3.0", sdk, "https://github.com/x/releases/tag/0.3.0")
            bucket.head_object.assert_called_with("cngist/CNGoldenLink-0.3.0.zip")
            key, body = bucket.put_object.call_args.args
            self.assertEqual(key, "cngist/CNGoldenLink-latest.json")
            self.assertEqual(manifest["downloadUrl"], "https://aliyun-static.diving-fish.com/cngist/CNGoldenLink-0.3.0.zip")
            self.assertIn(b'"version": "0.3.0"', body)
            self.assertIn("max-age=300", bucket.put_object.call_args.kwargs["headers"]["Cache-Control"])
            with self.assertRaisesRegex(ValueError, "major.minor.patch"):
                module.publish_latest("latest", sdk)

    def test_missing_credentials_and_invalid_filename(self):
        with tempfile.TemporaryDirectory() as directory, patch("builtins.print"), patch.dict(os.environ, {}, clear=True):
            package = Path(directory) / "CNGoldenLink-0.1.0.zip"
            package.touch()
            with self.assertRaisesRegex(ValueError, "Missing required secret"):
                module.upload(package, Mock())
            with self.assertRaisesRegex(ValueError, "versioned"):
                module.upload(Path(directory) / "latest.zip", Mock())


if __name__ == "__main__":
    unittest.main()
