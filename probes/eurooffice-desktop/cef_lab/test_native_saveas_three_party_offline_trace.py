"""GD31J27ag deferred synthetic-only three-party event trace tests.

No live Astra VM, Python/C++ tests, kernel calls, services, IPC or documents.
This is a replay *parser*; a forged trace must never grant save authority.
"""
import copy
import hashlib
import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_three_party_offline_trace as trace

RUN="a"*32
SHA=hashlib.sha256(b"approved synthetic fixture").hexdigest()

def payload():
    return {
        "schema":trace.TRACE_SCHEMA,
        "source":"synthetic-operator-replay-untrusted",
        "mode":"inspect_only",
        "runId":RUN,
        "fixtureSha256":SHA,
        "documentAccessCount":0,
        "status":"denied",
        "events":[
            {"step":index+1,"role":role,"event":event,"result":result}
            for index,(role,event,result) in enumerate(trace.EXPECTED_EVENTS)
        ],
    }

def raw(data):
    return (json.dumps(data,ensure_ascii=True,separators=(",",":"),
                       sort_keys=True)+"\n").encode("ascii")

class StrictTraceTests(unittest.TestCase):
    def test_exact_three_party_denial_only_trace_never_attests(self):
        reporter=trace.OneShotTraceReview()
        result=reporter.inspect(raw(payload()),expected_run_id=RUN,
                                expected_fixture_sha256=SHA)
        self.assertTrue(reporter.consumed)
        self.assertTrue(result["traceOrderObserved"])
        self.assertTrue(result["noDocumentAccessClaimObserved"])
        self.assertEqual("requires-independent-review",result["state"])
        for field in (
            "osEvidenceAttested","transportAttested",
            "workerExecutableAttested","trustedVmIsolationAttested",
            "trustedSupervisorAttested","nativePublicationAuthorized",
            "writePermissionGranted","releaseReady","approvedForProduction",
        ):
            self.assertIs(result[field],False)

    def test_wrong_step_order_duplicate_missing_or_sender_denial_fails(self):
        original=payload()
        cases=[]
        x=copy.deepcopy(original);x["events"][2],x["events"][3]=x["events"][3],x["events"][2];cases.append(x)
        x=copy.deepcopy(original);x["events"].append(x["events"][-1]);cases.append(x)
        x=copy.deepcopy(original);x["events"].pop();cases.append(x)
        x=copy.deepcopy(original);x["events"][4]["step"]=4;cases.append(x)
        x=copy.deepcopy(original);x["events"][6]["result"]="success";cases.append(x)
        x=copy.deepcopy(original);x["events"][1]["role"]="worker";cases.append(x)
        x=copy.deepcopy(original);x["events"][5]["event"]="saveas";cases.append(x)
        x=copy.deepcopy(original);x["events"][7]["nativePublicationAuthorized"]=True;cases.append(x)
        for item in cases:
            with self.subTest(item=item["events"][-1]):
                with self.assertRaises(trace.TraceError):
                    trace.OneShotTraceReview().inspect(
                        raw(item),expected_run_id=RUN,
                        expected_fixture_sha256=SHA)

    def test_dangerous_document_mismatch_and_approval_fields_rejected(self):
        base=payload()
        changes=[
            ("documentAccessCount",1),
            ("documentAccessCount",True),
            ("status","saved"),
            ("source","actual-systemd-attested"),
            ("mode","publish"),
            ("runId","b"*32),
            ("fixtureSha256","0"*64),
        ]
        for key,value in changes:
            item=copy.deepcopy(base);item[key]=value
            with self.subTest(key=key),self.assertRaises(trace.TraceError):
                trace.OneShotTraceReview().inspect(
                    raw(item),expected_run_id=RUN,expected_fixture_sha256=SHA)
        for key in ("releaseReady","documentPath","rootAttested","writePermissionGranted"):
            item=copy.deepcopy(base);item[key]=True
            with self.subTest(key=key),self.assertRaises(trace.TraceError):
                trace.OneShotTraceReview().inspect(
                    raw(item),expected_run_id=RUN,expected_fixture_sha256=SHA)

    def test_bad_json_duplicates_noncanonical_oversize_and_replay_refused(self):
        baseline=raw(payload())
        duplicate=baseline.replace(b'"status":"denied"',
                                   b'"status":"denied","status":"saved"')
        bad=[
            duplicate,
            baseline[:-1],
            b" "+baseline,
            baseline+b"\n",
            baseline.replace(b'"documentAccessCount":0',
                             b'"documentAccessCount": 0'),
            b"x"*(trace.MAX_TRACE_BYTES+1),
            b'{"x":NaN}\n',
        ]
        for item in bad:
            with self.subTest(item=item[:36]),self.assertRaises(trace.TraceError):
                trace.OneShotTraceReview().inspect(
                    item,expected_run_id=RUN,expected_fixture_sha256=SHA)
        session=trace.OneShotTraceReview()
        with self.assertRaises(trace.TraceError):
            session.inspect(raw({**payload(),"status":"saved"}),
                            expected_run_id=RUN,expected_fixture_sha256=SHA)
        self.assertTrue(session.consumed)
        with self.assertRaises(trace.TraceError):
            session.inspect(baseline,expected_run_id=RUN,
                            expected_fixture_sha256=SHA)

    def test_wrong_external_pins_and_bool_generation_refused_before_parse(self):
        good=raw(payload())
        for kwargs in (
            dict(expected_run_id="b"*32,expected_fixture_sha256=SHA),
            dict(expected_run_id=RUN,expected_fixture_sha256="0"*64),
            dict(expected_run_id=RUN,expected_fixture_sha256=1),
            dict(expected_run_id=True,expected_fixture_sha256=SHA),
        ):
            with self.subTest(kwargs=kwargs),self.assertRaises(trace.TraceError):
                trace.OneShotTraceReview().inspect(good,**kwargs)

if __name__=="__main__":
    unittest.main()
