import json
import unittest

import revit_mcp_next_inprocess as inprocess


class InProcessWrapperTests(unittest.TestCase):
    def setUp(self):
        self._original_execute = inprocess.execute
        self.requests = []

        def fake_execute(uiapp, request, addin_path=None):
            self.requests.append({"uiapp": uiapp, "request": request, "addin_path": addin_path})
            return {"ok": True, "data": {"request": request, "addinPath": addin_path}, "warnings": [], "metrics": {"elapsedMs": 1}}

        inprocess.execute = fake_execute

    def tearDown(self):
        inprocess.execute = self._original_execute

    def _assert_read_dispatch(self, operation, call):
        result = call()
        request = result["request"]
        self.assertEqual(request["operation"], operation)
        self.assertEqual(request["operationKind"], "read")
        self.assertEqual(request["payload"], {"limit": 1})
        self.assertEqual(request["documentFingerprint"], "doc-1")
        self.assertEqual(request["expectedGeneration"], 7)
        self.assertEqual(request["timeoutMs"], 1234)
        self.assertEqual(result["addinPath"], "addin.dll")

    def test_all_inprocess_read_wrappers_dispatch_expected_operations(self):
        wrappers = [
            ("list_documents", lambda: inprocess.list_documents("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("get_levels", lambda: inprocess.get_levels("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("get_views", lambda: inprocess.get_views("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("get_sheets", lambda: inprocess.get_sheets("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("get_schedules", lambda: inprocess.get_schedules("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("get_schedule_fields", lambda: inprocess.get_schedule_fields("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("get_current_view", lambda: inprocess.get_current_view("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("get_current_view_elements", lambda: inprocess.get_current_view_elements("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("get_selection", lambda: inprocess.get_selection("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("analyze_model", lambda: inprocess.analyze_model("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("get_model_readiness", lambda: inprocess.get_model_readiness("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("get_model_context", lambda: inprocess.get_model_context("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("get_material_quantities", lambda: inprocess.get_material_quantities("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("get_warnings", lambda: inprocess.get_warnings("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("get_rooms", lambda: inprocess.get_rooms("uiapp", {"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("query", lambda: inprocess.query("uiapp", payload={"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("describe_parameters", lambda: inprocess.describe_parameters("uiapp", payload={"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
            ("catalog", lambda: inprocess.catalog("uiapp", payload={"limit": 1}, document_fingerprint="doc-1", expected_generation=7, timeout_ms=1234, addin_path="addin.dll")),
        ]

        for operation, call in wrappers:
            self.requests = []
            self._assert_read_dispatch(operation, call)
            self.assertEqual(len(self.requests), 1)

    def test_payload_merge_does_not_mutate_input_and_omits_none(self):
        catalog_payload = {"kind": "old", "limit": 99}
        catalog_original = dict(catalog_payload)
        catalog_result = inprocess.catalog(
            "uiapp",
            kind="familySymbols",
            payload=catalog_payload,
            catalog_filter=None,
            include_total_count=False,
        )
        self.assertEqual(catalog_payload, catalog_original)
        merged_catalog = catalog_result["request"]["payload"]
        self.assertEqual(merged_catalog["kind"], "familySymbols")
        self.assertEqual(merged_catalog["limit"], 99)
        self.assertEqual(merged_catalog["includeTotalCount"], False)
        self.assertNotIn("filter", merged_catalog)

        query_payload = {"fields": ["old"], "limit": 2}
        query_original = dict(query_payload)
        query_result = inprocess.query("uiapp", payload=query_payload, query_filter=None, fields=["id"], preset=None)
        self.assertEqual(query_payload, query_original)
        merged_query = query_result["request"]["payload"]
        self.assertEqual(merged_query["fields"], ["id"])
        self.assertEqual(merged_query["limit"], 2)
        self.assertNotIn("filter", merged_query)
        self.assertNotIn("preset", merged_query)

        parameters_payload = {"filter": {"selectionOnly": False}, "limit": 3}
        parameters_original = dict(parameters_payload)
        parameters_result = inprocess.describe_parameters(
            "uiapp",
            payload=parameters_payload,
            parameter_filter={"selectionOnly": True},
            fields=None,
            include_total_count=False,
        )
        self.assertEqual(parameters_payload, parameters_original)
        merged_parameters = parameters_result["request"]["payload"]
        self.assertEqual(merged_parameters["filter"], {"selectionOnly": True})
        self.assertEqual(merged_parameters["limit"], 3)
        self.assertEqual(merged_parameters["includeTotalCount"], False)
        self.assertNotIn("fields", merged_parameters)

    def test_preview_apply_forward_guards_timeout_and_addin_path(self):
        change_set = {"transactionName": "test", "operations": []}
        preview = inprocess.preview_change_set(
            "uiapp",
            change_set,
            document_fingerprint="doc-1",
            expected_generation=7,
            addin_path="addin.dll",
            timeout_ms=2222,
        )
        preview_request = preview["request"]
        self.assertEqual(preview_request["operation"], "preview_change_set")
        self.assertEqual(preview_request["operationKind"], "preview")
        self.assertEqual(preview_request["documentFingerprint"], "doc-1")
        self.assertEqual(preview_request["expectedGeneration"], 7)
        self.assertEqual(preview_request["timeoutMs"], 2222)
        self.assertEqual(preview["addinPath"], "addin.dll")

        applied = inprocess.apply_change_set(
            "uiapp",
            change_set,
            document_fingerprint="doc-2",
            expected_generation=8,
            addin_path="addin2.dll",
            timeout_ms=3333,
        )
        apply_request = applied["request"]
        self.assertEqual(apply_request["operation"], "apply_change_set")
        self.assertEqual(apply_request["operationKind"], "write")
        self.assertEqual(apply_request["documentFingerprint"], "doc-2")
        self.assertEqual(apply_request["expectedGeneration"], 8)
        self.assertEqual(apply_request["timeoutMs"], 3333)
        self.assertEqual(applied["addinPath"], "addin2.dll")

    def test_apply_preview_does_not_mutate_change_set(self):
        change_set = {"transactionName": "test", "operations": []}
        original = {"transactionName": "test", "operations": []}
        preview = {"previewId": "preview-1"}

        applied = inprocess.apply_preview("uiapp", change_set, preview, addin_path="addin.dll")

        self.assertEqual(change_set, original)
        payload = applied["request"]["payload"]
        self.assertEqual(payload["previewId"], "preview-1")
        self.assertEqual(payload["confirm"], True)
        self.assertNotIn("changeSetHash", payload)
        self.assertNotIn("baseGeneration", payload)
        self.assertNotIn("expiresAt", payload)


class InProcessLegacyShapeTests(unittest.TestCase):
    def setUp(self):
        self._original_load_bridge = inprocess.load_bridge

    def tearDown(self):
        inprocess.load_bridge = self._original_load_bridge

    def test_legacy_status_execute_require_ok_shapes(self):
        requests = []

        class FakeBridge(object):
            @staticmethod
            def StatusJson(uiapp):
                return json.dumps({"ok": True, "data": {"connected": True, "uiapp": uiapp}})

            @staticmethod
            def ExecuteJson(uiapp, request):
                requests.append(request)
                return json.dumps({"ok": True, "data": {"request": json.loads(request), "uiapp": uiapp}})

        inprocess.load_bridge = lambda addin_path=None: FakeBridge

        status_response = inprocess.status("uiapp")
        self.assertEqual(status_response["ok"], True)
        self.assertEqual(status_response["data"]["connected"], True)

        dict_response = inprocess.execute("uiapp", {"operation": "status"})
        self.assertEqual(dict_response["data"]["request"]["operation"], "status")
        string_response = inprocess.execute("uiapp", '{"operation":"get_levels"}')
        self.assertEqual(string_response["data"]["request"]["operation"], "get_levels")
        self.assertEqual(len(requests), 2)

        self.assertEqual(inprocess.require_ok({"ok": True}, "empty"), {})
        with self.assertRaises(RuntimeError) as context:
            inprocess.require_ok(
                {
                    "ok": False,
                    "error": {
                        "code": "TEST_ERROR",
                        "message": "Failed on purpose.",
                        "suggestedNextAction": "Retry after refreshing document state.",
                    },
                },
                "test_operation",
            )
        self.assertIn("TEST_ERROR", str(context.exception))
        self.assertIn("Retry after refreshing document state.", str(context.exception))


if __name__ == "__main__":
    unittest.main()
