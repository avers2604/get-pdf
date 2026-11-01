"""GD-31J.27e: pure reference model for the *synthetic* pinned v2 callback gate.

No native process, document input, SDK or persistence. This model specifies a
deny-once-deny-forever lifecycle and emits parser-compatible untrusted receipts.
The C++ owner-thread gate is checked separately by static contracts and VM E2E.
"""
from __future__ import annotations

MAX_EVENTS = 64

class NativeV2State:
    def __init__(self):
        self.armed = False
        self.pending = False
        self.answered = False
        self.aborted = False
        self.closed = False
        self.timed_out = False
        self.source_unchanged = True
        self.dialog_count = 0
        self.on_save_count = 0
        self.event_serial = 0
        self.first_dialog_sequence = 0
        self.first_on_save_sequence = 0
        self.valid_dialog_count = 0
        self.invalid_dialog = False
        self.unexpected_on_save = False
        self.saved_after_answer = False
        self.cancelled_after_answer = False

    def _tick(self):
        self.event_serial = min(MAX_EVENTS, self.event_serial + 1)
        return self.event_serial

    def arm(self, *, ready: bool):
        if (not ready or self.aborted or self.closed or self.timed_out or
                self.answered or self.pending or self.dialog_count or self.on_save_count):
            return False
        self.armed = True
        return True

    def dialog(self, *, valid: bool):
        seq = self._tick()
        self.dialog_count = min(MAX_EVENTS, self.dialog_count + 1)
        if self.first_dialog_sequence == 0:
            self.first_dialog_sequence = seq
        allowed = self.armed and self.dialog_count == 1 and not self.aborted and not self.closed
        self.armed = False
        if valid:
            self.valid_dialog_count = min(MAX_EVENTS, self.valid_dialog_count + 1)
        if not allowed or not valid:
            if self.dialog_count == 1 or not valid:
                self.invalid_dialog = True
            self.aborted = True
            self.pending = False
            return False
        self.pending = True
        return True

    def answer(self, *, ready: bool):
        if (not ready or not self.pending or self.answered or self.aborted or
                self.closed or self.timed_out or self.dialog_count != 1 or
                self.on_save_count != 0):
            return False
        self.pending = False
        self.answered = True
        return True

    def on_save(self, *, cancelled: bool):
        seq = self._tick()
        self.on_save_count = min(MAX_EVENTS, self.on_save_count + 1)
        if self.first_on_save_sequence == 0:
            self.first_on_save_sequence = seq
        if not self.answered or self.closed or self.timed_out:
            self.unexpected_on_save = True
            self.aborted = True
        elif self.on_save_count != 1:
            self.aborted = True
        if cancelled:
            if self.answered:
                self.cancelled_after_answer = True
            self.aborted = True
        elif self.answered:
            self.saved_after_answer = True

    def timeout(self):
        self.timed_out = True
        self.aborted = True
        self.armed = False
        self.pending = False

    def close(self):
        # A *normal* closed CEF view is required before quarantine publication.
        # It must not erase an already clean acknowledgement. Late events
        # after closure, however, must never reopen a candidate.
        self.closed = True
        self.armed = False
        self.pending = False

    def sequence(self):
        return (self.dialog_count, self.on_save_count,
                self.first_dialog_sequence, self.first_on_save_sequence)

    def can_publish(self):
        return (
            not self.aborted and not self.timed_out and self.source_unchanged
            and self.valid_dialog_count == 1 and self.answered and
            self.saved_after_answer and not self.cancelled_after_answer
            and not self.invalid_dialog and not self.unexpected_on_save
            and self.sequence() == (1, 1, 1, 2)
        )

    def reason(self, *, published: bool):
        if published and not self.can_publish():
            raise ValueError("Cannot assert publication of a noneligible native candidate")
        if self.invalid_dialog:
            return "invalid_dialog"
        if self.dialog_count > 1:
            return "duplicate_dialog"
        if self.unexpected_on_save:
            return "unexpected_on_save"
        if self.on_save_count > 1:
            return "duplicate_on_save"
        if self.cancelled_after_answer:
            return "cancelled"
        if not self.source_unchanged:
            return "source_modified"
        if self.dialog_count == 0:
            return "dialog_missing"
        if not self.answered:
            return "target_not_assigned"
        if self.timed_out:
            return "timeout"
        if not self.saved_after_answer:
            return "save_not_acknowledged"
        return "candidate_untrusted" if published else "candidate_missing"

    def receipt(self, session: str, tab: str, fixture: str,
                *, published: bool = False, sha: str = ""):
        if published and (not self.can_publish() or len(sha) != 64):
            raise ValueError("Synthetic output cannot be asserted")
        phase = ("published_disposable" if published else
                 "on_save_observed" if self.on_save_count else
                 "target_assigned" if self.answered else
                 "dialog_observed" if self.dialog_count else "not_started")
        return {
            "schema": "getdoc-native-saveas-pinned-event-lab/2",
            "sessionId": session, "tabId": tab, "fixtureName": fixture,
            "failureCode": self.reason(published=published), "phase": phase,
            "dialogCount": self.dialog_count,
            "onSaveCount": self.on_save_count,
            "dialogSequence": self.first_dialog_sequence,
            "onSaveSequence": self.first_on_save_sequence,
            "invalidDialogObserved": self.invalid_dialog,
            "unexpectedOnSaveObserved": self.unexpected_on_save,
            "receipt": {
                "schema": "getdoc-native-saveas-candidate-lab/1",
                "sessionId": session, "tabId": tab, "fixtureName": fixture,
                "nativeSaveRequestObserved": self.valid_dialog_count == 1,
                "savePathProvided": self.answered,
                "nativeOnSaveAfterAnswer": self.saved_after_answer,
                "saveCancelledAfterAnswer": self.cancelled_after_answer,
                "sourceUnchanged": self.source_unchanged,
                "publishedToDisposableOutput": published,
                "outputSha256": sha if published else "",
                "nativeSaveProven": False, "documentVerified": False,
                "saveVerified": False, "pdfVerified": False,
                "printApproved": False, "approvedForProduction": False,
                "laboratoryOnly": True,
            },
        }
