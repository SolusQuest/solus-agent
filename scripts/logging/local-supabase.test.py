"""Offline operator-admission regressions. No Docker, network or real credentials."""
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import tempfile
import unittest
from shlex import quote

HERE = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('local_supabase', HERE / 'local-supabase.py')
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


class LocalAdmission(unittest.TestCase):
    def test_remote_docker_endpoints_refuse(self):
        bridge.require_local_endpoint('unix:///tmp/synthetic-docker.sock')
        for value in ['tcp://127.0.0.1:2375', 'ssh://remote', 'https://remote', '', None]:
            with self.subTest(endpoint=value), self.assertRaises(ValueError):
                bridge.require_local_endpoint(value)

    def test_missing_explicit_budget_refuses_before_any_docker_call(self):
        with tempfile.TemporaryDirectory() as directory:
            bin_dir = pathlib.Path(directory)
            marker = bin_dir / 'docker-was-called'
            docker = bin_dir / 'docker'
            docker.write_text('#!/bin/sh\n: > ' + quote(str(marker)) + '\nexit 99\n')
            docker.chmod(0o700)
            import os
            result = subprocess.run(
                [os.sys.executable, str(HERE / 'local-supabase.py'), '--source', '/synthetic',
                 '--inputs', '/synthetic', '--sdk', '/synthetic', '--cache', directory,
                 '--output', str(bin_dir / 'must-not-exist')],
                env={'PATH': str(bin_dir)}, capture_output=True, text=True)
            self.assertEqual(result.returncode, 2)
            self.assertFalse(marker.exists())
            self.assertFalse((bin_dir / 'must-not-exist').exists())

    def test_actual_git_and_archive_boundary_rejects_dirty_or_substituted_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            source, inputs = root / 'source', root / 'inputs'
            source.mkdir(); inputs.mkdir()
            (source / 'action.mjs').write_text('// synthetic archive-boundary fixture\n')
            schema = source / 'database/logging/001_telemetry.sql'
            schema.parent.mkdir(parents=True)
            original_schema = b'-- original accepted schema\n'
            schema.write_bytes(original_schema)
            current_schema = root / 'database/logging/001_telemetry.sql'
            current_schema.parent.mkdir(parents=True)
            current_schema.write_bytes(b'-- incompatible current checkout schema\n')
            def git(*args):
                return subprocess.check_output(
                    ['git', '-c', 'user.name=Synthetic', '-c', 'user.email=synthetic@example.invalid',
                     '-c', 'commit.gpgsign=false', '-C', str(source), *args], stderr=subprocess.DEVNULL)
            git('init'); git('add', '.'); git('commit', '-m', 'synthetic')
            head = git('rev-parse', 'HEAD').decode().strip()
            names = ['Fixture.nupkg', 'Api.4.nupkg', 'solus-logging-cli-0.1.0-internal.1-linux-x64.tar.gz']
            for name in names: (inputs / name).write_bytes(('synthetic-' + name).encode())
            sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
            manifest = {'source': {'commit': head}, 'packages': [{'file': names[0], 'sha256': sha(inputs / names[0])}],
                        'agentApi': {'id': 'Api', 'version': '4', 'packageSha256': sha(inputs / names[1])},
                        'payload': {'sha256': sha(inputs / names[2])},
                        'action': {'files': {'action.mjs': sha(source / 'action.mjs')}}}
            folder = root / 'scripts/logging'; folder.mkdir(parents=True)
            (folder / 'accepted-candidate.json').write_text(json.dumps({'manifest': manifest}))
            sdk = root / 'sdk'; sdk.write_bytes(b'synthetic SDK fixture, not an executable archive')
            old_root, old_sdk = bridge.ROOT, bridge.SDK_SHA512
            try:
                bridge.ROOT = root; bridge.SDK_SHA512 = hashlib.sha512(sdk.read_bytes()).hexdigest()
                admitted_schema = bridge.verify_inputs(source, inputs, sdk)
                self.assertEqual(admitted_schema, original_schema)
                self.assertNotEqual(admitted_schema, current_schema.read_bytes())
                schema.write_bytes(b'-- changed after admission\n')
                self.assertEqual(admitted_schema, original_schema)
                with self.assertRaisesRegex(ValueError, 'clean'): bridge.verify_inputs(source, inputs, sdk)
                git('checkout', '--', 'database/logging/001_telemetry.sql')
                (source / 'action.mjs').write_text('// changed\n')
                with self.assertRaisesRegex(ValueError, 'clean'): bridge.verify_inputs(source, inputs, sdk)
                git('checkout', '--', 'action.mjs')
                original = (inputs / names[0]).read_bytes(); (inputs / names[0]).write_bytes(original + b'changed')
                with self.assertRaisesRegex(ValueError, 'archive hash'): bridge.verify_inputs(source, inputs, sdk)
                (inputs / names[0]).write_bytes(original); sdk.write_bytes(b'changed SDK')
                with self.assertRaisesRegex(ValueError, 'SDK109 hash'): bridge.verify_inputs(source, inputs, sdk)
            finally:
                bridge.ROOT, bridge.SDK_SHA512 = old_root, old_sdk


if __name__ == '__main__':
    unittest.main()
