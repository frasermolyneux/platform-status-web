from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import image_inputs


PATTERN = (Path(__file__).resolve().parents[1] / ".github" / "image-input-pattern.txt").read_text().strip()
PREVIOUS = "a" * 40


class ImageInputTests(unittest.TestCase):
    def test_unpublished_source_from_earlier_commits_is_not_lost_to_a_final_documentation_commit(self):
        with patch.object(image_inputs, "last_published_revision", return_value=PREVIOUS), \
                patch.object(image_inputs.subprocess, "check_output", return_value="src/Feature.cs\0README.md\0") as git:
            self.assertTrue(image_inputs.should_publish("owner/repo", PATTERN))
        git.assert_called_once_with(
            ["git", "diff", "--no-renames", "--name-only", "-z", PREVIOUS, "HEAD"],
            text=True, encoding="utf-8"
        )

    def test_manifest_and_documentation_changes_retain_images(self):
        with patch.object(image_inputs, "last_published_revision", return_value=PREVIOUS), \
                patch.object(image_inputs.subprocess, "check_output", return_value="workload.json\0README.md\0"):
            self.assertFalse(image_inputs.should_publish("owner/repo", PATTERN))

    def assert_git_change_requires_publication(self, original_name, renamed_name=None):
        with tempfile.TemporaryDirectory(prefix="status-image-inputs-") as directory:
            root = Path(directory)

            def git(*arguments):
                return subprocess.run(
                    ["git", *arguments], cwd=root, check=True,
                    stdout=subprocess.PIPE, text=True, encoding="utf-8"
                ).stdout

            git("init", "--quiet")
            git("config", "core.quotepath", "true")
            git("config", "diff.renames", "true")
            original = root / original_name
            original.parent.mkdir(parents=True)
            original.write_text("before\n", encoding="utf-8")
            git("add", ".")
            git("-c", "user.name=Status Tests", "-c", "user.email=status-tests@example.invalid",
                "commit", "--quiet", "-m", "Initial image inputs")
            previous = git("rev-parse", "HEAD").strip()
            if renamed_name:
                renamed = root / renamed_name
                renamed.parent.mkdir(parents=True)
                original.rename(renamed)
            else:
                original.write_text("after\n", encoding="utf-8")
            git("add", "-A")
            git("-c", "user.name=Status Tests", "-c", "user.email=status-tests@example.invalid",
                "commit", "--quiet", "-m", "Change image inputs")

            with patch.object(image_inputs, "last_published_revision", return_value=previous), \
                    patch.object(image_inputs.subprocess, "check_output", side_effect=lambda command, **kwargs:
                        subprocess.run(command, cwd=root, check=True, stdout=subprocess.PIPE, **kwargs).stdout):
                self.assertTrue(image_inputs.should_publish("owner/repo", PATTERN))

    def test_renaming_a_file_out_of_the_image_context_requires_publication(self):
        self.assert_git_change_requires_publication("src/logo.svg", "docs/logo.svg")

    def test_non_ascii_source_paths_require_publication(self):
        self.assert_git_change_requires_publication("src/caf\u00e9.cs")

    def test_provenance_uses_recorded_build_source_not_the_workflow_run_head(self):
        with patch.object(image_inputs, "gh_json", side_effect=[
            {"workflow_runs": [{"id": 2, "head_sha": "b" * 40}, {"id": 1, "head_sha": "c" * 40}]},
            {"total_count": 0, "artifacts": []},
            {"total_count": 1, "artifacts": [{"name": f"status-image-digests-{PREVIOUS}", "expired": False}]},
        ]):
            self.assertEqual(image_inputs.last_published_revision("owner/repo"), PREVIOUS)

    def test_missing_provenance_requires_a_full_publication(self):
        with patch.object(image_inputs, "last_published_revision", return_value=None):
            self.assertTrue(image_inputs.should_publish("owner/repo", PATTERN))

    def test_expired_digest_metadata_does_not_suppress_a_full_publication(self):
        with patch.object(image_inputs, "gh_json", side_effect=[
            {"workflow_runs": [{"id": 1}]},
            {"total_count": 1, "artifacts": [{"name": f"status-image-digests-{PREVIOUS}", "expired": True}]},
            {"workflow_runs": []},
        ]):
            self.assertTrue(image_inputs.should_publish("owner/repo", PATTERN))


if __name__ == "__main__":
    unittest.main()
