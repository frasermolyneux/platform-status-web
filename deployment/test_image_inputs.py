from pathlib import Path
import unittest
from unittest.mock import patch

import image_inputs


PATTERN = (Path(__file__).resolve().parents[1] / ".github" / "image-input-pattern.txt").read_text().strip()
PREVIOUS = "a" * 40


class ImageInputTests(unittest.TestCase):
    def test_unpublished_source_from_earlier_commits_is_not_lost_to_a_final_documentation_commit(self):
        with patch.object(image_inputs, "last_published_revision", return_value=PREVIOUS), \
                patch.object(image_inputs.subprocess, "check_output", return_value="src/Feature.cs\nREADME.md\n") as git:
            self.assertTrue(image_inputs.should_publish("owner/repo", PATTERN))
        git.assert_called_once_with(["git", "diff", "--name-only", PREVIOUS, "HEAD"], text=True)

    def test_manifest_and_documentation_changes_retain_images(self):
        with patch.object(image_inputs, "last_published_revision", return_value=PREVIOUS), \
                patch.object(image_inputs.subprocess, "check_output", return_value="workload.json\nREADME.md\n"):
            self.assertFalse(image_inputs.should_publish("owner/repo", PATTERN))

    def test_provenance_uses_recorded_build_source_not_the_workflow_run_head(self):
        with patch.object(image_inputs, "gh_json", side_effect=[
            {"workflow_runs": [{"id": 2, "head_sha": "b" * 40}, {"id": 1, "head_sha": "c" * 40}]},
            {"total_count": 0, "artifacts": []},
            {"total_count": 1, "artifacts": [{"name": f"status-image-digests-{PREVIOUS}"}]},
        ]):
            self.assertEqual(image_inputs.last_published_revision("owner/repo"), PREVIOUS)

    def test_missing_provenance_requires_a_full_publication(self):
        with patch.object(image_inputs, "last_published_revision", return_value=None):
            self.assertTrue(image_inputs.should_publish("owner/repo", PATTERN))


if __name__ == "__main__":
    unittest.main()
