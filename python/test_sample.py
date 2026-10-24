"""The sample against the stub: no server, no credential, nothing leaves this machine.

    python -m unittest
"""

from __future__ import annotations

import io
import unittest

from dataoil import DataOilClient, DataOilNotFoundError

import sample
from stub_engine import SandboxStub, StubEngine


def client(stub: StubEngine) -> DataOilClient:
    return DataOilClient(stub.url, "sample_notes", token="dot_placeholder", job_id="sample-tests")


class WalkthroughTests(unittest.TestCase):
    def test_every_step_runs_and_answers_what_was_written(self) -> None:
        with StubEngine() as stub:
            result = sample.run(client(stub), True, io.StringIO(), "SampleNote_test")
        self.assertTrue(result.created_database)
        self.assertEqual(4, result.written)
        self.assertEqual(["Casing failure on well 7", "Permit to work audit"], result.highly_rated)
        self.assertEqual(4, result.streamed)
        self.assertEqual("Casing failure on well 7", result.keyword_hit)
        self.assertEqual("Casing failure on well 7", result.nearest_hit)
        self.assertTrue(stub.every_request_carried_the_headers)

    def test_the_collection_and_a_database_the_run_created_are_dropped(self) -> None:
        with StubEngine() as stub:
            sample.run(client(stub), True, io.StringIO(), "SampleNote_test")
        self.assertEqual("POST /api/v2/databases", stub.requests[1])
        self.assertEqual("DELETE /api/v2/databases/sample_notes", stub.requests[-1])
        self.assertFalse(stub.database_exists)

    def test_a_database_that_was_already_there_is_never_dropped(self) -> None:
        with StubEngine(database_exists=True) as stub:
            result = sample.run(client(stub), True, io.StringIO(), "SampleNote_test")
        self.assertFalse(result.created_database)
        self.assertNotIn("POST /api/v2/databases", stub.requests)
        self.assertFalse(any(r.startswith("DELETE /api/v2/databases") for r in stub.requests))

    def test_a_missing_database_is_not_created_unless_asked(self) -> None:
        with StubEngine() as stub:
            with self.assertRaises(DataOilNotFoundError):
                sample.run(client(stub), False, io.StringIO(), "SampleNote_test")
        self.assertEqual(["GET /api/v2/databases/sample_notes/schema/version"], stub.requests)

    def test_the_dry_run_exits_cleanly(self) -> None:
        out = io.StringIO()
        self.assertEqual(0, sample.main(["--dry-run"], {}, out, io.StringIO()))
        self.assertIn("Done: 4 notes written", out.getvalue())


def sandbox_client(stub: SandboxStub) -> DataOilClient:
    return DataOilClient(stub.url, "Sandbox", username="suser", password="placeholder", job_id="sample-tests")


class SandboxTests(unittest.TestCase):
    def test_the_read_only_walkthrough_reads_and_the_write_is_refused(self) -> None:
        out = io.StringIO()
        with SandboxStub() as stub:
            result = sample.run_sandbox(sandbox_client(stub), out)
        self.assertEqual(["Sandbox"], result.databases)
        self.assertIn("WorldBank_rows", result.collections)
        self.assertNotIn("V", result.collections)
        self.assertIn("GDP (USD)", result.columns)
        self.assertEqual(["Italy", "Nigeria", "India", "Japan"], result.top_gdp)
        self.assertEqual(2, result.japan_rows)
        self.assertEqual("Japan", result.longest_lived[0])
        self.assertEqual(["<2es>", "<2fr>"], result.languages)
        self.assertEqual("403 Security", result.write_refused)
        self.assertIn("5. Write refused as expected: 403 Security", out.getvalue())
        self.assertTrue(stub.every_request_carried_the_headers)

    def test_the_walkthrough_sends_one_light_request_per_step(self) -> None:
        with SandboxStub() as stub:
            sample.run_sandbox(sandbox_client(stub), io.StringIO())
        self.assertEqual(["GET /api/v2/databases", "GET /api/v2/databases/Sandbox/schema"], stub.requests[:2])
        self.assertEqual(7, len(stub.requests))


class SettingsTests(unittest.TestCase):
    def test_no_variables_mean_the_public_sandbox_read_only(self) -> None:
        settings, problems = sample.Settings.from_environment({})
        self.assertEqual([], problems)
        assert settings is not None
        self.assertTrue(settings.sandbox)
        self.assertEqual("https://sandbox.dataoil.tech", settings.url)
        self.assertEqual("Sandbox", settings.database)
        self.assertEqual("suser", settings.username)
        self.assertIsNone(settings.token)
        self.assertFalse(settings.create_database)

    def test_the_sandbox_keeps_the_job_id_and_ignores_the_create_switch(self) -> None:
        settings, _ = sample.Settings.from_environment({"DATAOIL_JOB_ID": "my-job", "DATAOIL_CREATE_DATABASE": "true"})
        assert settings is not None
        self.assertTrue(settings.sandbox)
        self.assertEqual("my-job", settings.job_id)
        self.assertFalse(settings.create_database)

    def test_naming_any_connection_variable_leaves_the_sandbox_and_names_the_rest(self) -> None:
        settings, problems = sample.Settings.from_environment({"DATAOIL_URL": "https://acme.dataoil.tech"})
        self.assertIsNone(settings)
        self.assertEqual(2, len(problems))
        self.assertTrue(problems[0].startswith("DATAOIL_DATABASE"))
        err = io.StringIO()
        self.assertEqual(1, sample.main([], {"DATAOIL_TOKEN": "dot_placeholder"}, io.StringIO(), err))
        self.assertIn("DATAOIL_URL", err.getvalue())

    def test_your_own_tenant_runs_the_write_walkthrough(self) -> None:
        settings, _ = sample.Settings.from_environment({
            "DATAOIL_URL": "https://acme.dataoil.tech", "DATAOIL_DATABASE": "sample_notes", "DATAOIL_TOKEN": "dot_placeholder"})
        assert settings is not None
        self.assertFalse(settings.sandbox)

    def test_the_sandbox_named_explicitly_is_still_read_only(self) -> None:
        settings, _ = sample.Settings.from_environment({
            "DATAOIL_URL": "https://SANDBOX.dataoil.tech/", "DATAOIL_DATABASE": "Sandbox",
            "DATAOIL_USERNAME": "suser", "DATAOIL_PASSWORD": "placeholder"})
        assert settings is not None
        self.assertTrue(settings.sandbox)

    def test_a_name_and_password_and_the_create_switch(self) -> None:
        settings, problems = sample.Settings.from_environment({
            "DATAOIL_URL": "https://acme.dataoil.tech", "DATAOIL_DATABASE": "sample_notes",
            "DATAOIL_USERNAME": "someone", "DATAOIL_PASSWORD": "placeholder", "DATAOIL_CREATE_DATABASE": "TRUE",
        })
        self.assertEqual([], problems)
        assert settings is not None
        self.assertTrue(settings.create_database)
        self.assertIsNone(settings.token)
        self.assertEqual("sample-python-client", settings.job_id)

    def test_a_token_and_a_name_together_are_refused(self) -> None:
        settings, problems = sample.Settings.from_environment({
            "DATAOIL_URL": "https://acme.dataoil.tech", "DATAOIL_DATABASE": "sample_notes",
            "DATAOIL_TOKEN": "dot_placeholder", "DATAOIL_USERNAME": "someone",
        })
        self.assertIsNone(settings)
        self.assertEqual(1, len(problems))
        self.assertIn("not both", problems[0])

    def test_the_toy_embedding_points_a_drilling_question_at_drilling(self) -> None:
        self.assertEqual([4.0, 0.0, 0.0, 0.0], sample.toy_embed("drilling the well: casing and cement"))


if __name__ == "__main__":
    unittest.main()
