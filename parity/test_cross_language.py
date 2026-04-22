import json
import subprocess
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
FIXTURES_DIR = REPO_ROOT / "tests" / "fixtures"


class CrossLanguageParityTests(unittest.TestCase):
    def test_fixtures_match_across_typescript_and_csharp(self) -> None:
        fixture_paths = sorted(FIXTURES_DIR.glob("*.json"))
        self.assertTrue(fixture_paths, "No fixture files found.")

        for fixture_path in fixture_paths:
            with self.subTest(fixture=fixture_path.name):
                ts_result = self._run_typescript(fixture_path)
                cs_result = self._run_csharp(fixture_path)
                self.assertEqual(ts_result, cs_result)

    def _run_typescript(self, fixture_path: Path) -> dict:
        completed = subprocess.run(
            [
                "node",
                "--experimental-strip-types",
                "typescript/tools/canonicalize-fixture.ts",
                str(fixture_path),
            ],
            cwd=REPO_ROOT,
            check=True,
            capture_output=True,
            text=True,
        )
        return self._parse_json_output(completed.stdout)

    def _run_csharp(self, fixture_path: Path) -> dict:
        completed = subprocess.run(
            [
                "dotnet",
                "run",
                "--no-restore",
                "--project",
                "csharp/tools/RequestCanonicalizer.Cli/RequestCanonicalizer.Cli.csproj",
                "--",
                str(fixture_path),
            ],
            cwd=REPO_ROOT,
            check=True,
            capture_output=True,
            text=True,
        )
        return self._parse_json_output(completed.stdout)

    def _parse_json_output(self, output: str) -> dict:
        lines = [line for line in output.splitlines() if line.strip()]
        self.assertTrue(lines, "Command did not produce any output.")
        return json.loads(lines[-1])


if __name__ == "__main__":
    unittest.main()
