// GD-31J.8: process-level Qt CORE diagnostic IPC, not an Office runtime.
//
// The only supported commands are hello, attach-tab, resize and close.
// No file path, URL, printer, window handle, shell command or document data
// is processed. A Qt QWidget / CEF editor is never created here.
// This process MUST NOT be used as proof that native editor embedding works.
#include "ipc_probe.h"
#include <QByteArray>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QJsonParseError>
#include <QString>
#include <cmath>
#include <cstdio>

namespace {
constexpr char kProtocol[] = "getdoc-eurodesktop-ipc/1";
constexpr char kPinnedCommit[] = "6a671bb1ad238c61965a64de3a005c420940c814";
constexpr int kMaxBytes = 4096;

bool ReadLine(QByteArray& line) {
    line.clear();
    for (int i = 0; i < kMaxBytes; ++i) {
        const int c = std::fgetc(stdin);
        if (c == EOF) return false;
        if (c == '\n') return true;
        if (c == '\r' || c == 0) return false;
        line.append(static_cast<char>(c));
    }
    return false;
}

bool IsGuid32(const QString& value) {
    if (value.size() != 32 || value == QString(32, QChar('0'))) return false;
    for (QChar c : value) {
        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            return false;
    }
    return true;
}

bool ExactObject(const QJsonObject& object,
                 std::initializer_list<const char*> expected) {
    if (object.size() != static_cast<int>(expected.size())) return false;
    for (const char* name : expected)
        if (!object.contains(name)) return false;
    return true;
}

bool ParseCanonical(const QByteArray& bytes, QJsonObject& object) {
    QJsonParseError parse;
    const QJsonDocument document = QJsonDocument::fromJson(bytes, &parse);
    if (parse.error != QJsonParseError::NoError || !document.isObject()) return false;
    object = document.object();

    // Qt canonicalizes object key order. Reject duplicate JSON keys, unknown
    // whitespace/encodings and noncanonical numbers by comparing the exact
    // original request with the canonical document serialization.
    return QJsonDocument(object).toJson(QJsonDocument::Compact) == bytes;
}

bool Emit(QJsonObject object) {
    const QByteArray bytes = QJsonDocument(object).toJson(QJsonDocument::Compact);
    if (std::fwrite(bytes.constData(), 1, static_cast<size_t>(bytes.size()), stdout) !=
        static_cast<size_t>(bytes.size())) return false;
    if (std::fputc('\n', stdout) == EOF) return false;
    return std::fflush(stdout) == 0;
}

bool ValidInt(const QJsonValue& value, int min, int max) {
    if (!value.isDouble()) return false;
    const double number = value.toDouble();
    return std::isfinite(number) && number >= min && number <= max &&
           std::floor(number) == number;
}

QJsonObject Ack(const QString& command, const QString& session,
                const QString& state, int sequence, const QString& tab = {}) {
    QJsonObject ack{
        {"command", command},
        {"protocol", kProtocol},
        {"sequence", sequence},
        {"sessionId", session},
        {"state", state}
    };
    if (tab.isEmpty()) ack.insert("features", QJsonArray{});
    else ack.insert("tabId", tab);
    return ack;
}
} // namespace

int RunIpcProbe() {
    QByteArray bytes;
    QJsonObject request;
    if (!ReadLine(bytes) || !ParseCanonical(bytes, request) ||
        !ExactObject(request, {"command", "protocol", "sessionId", "upstreamCommit"}) ||
        request.value("command").toString() != "hello" ||
        request.value("protocol").toString() != kProtocol ||
        request.value("upstreamCommit").toString() != kPinnedCommit)
        return 2;

    const QString session = request.value("sessionId").toString();
    if (!IsGuid32(session) || !Emit(Ack("hello-ack", session, "ready", 0)))
        return 2;

    QString tabId;
    bool attached = false;
    int lastSeq = 0;
    for (int messages = 0; messages < 128; ++messages) {
        if (!ReadLine(bytes) || !ParseCanonical(bytes, request))
            return 2;
        const QString command = request.value("command").toString();
        if (request.value("protocol").toString() != kProtocol ||
            request.value("sessionId").toString() != session ||
            !ValidInt(request.value("sequence"), lastSeq + 1, lastSeq + 1))
            return 2;

        const QString incomingTab = request.value("tabId").toString();
        if (!IsGuid32(incomingTab) ||
            (attached && incomingTab != tabId))
            return 2;

        ++lastSeq;
        if (command == "attach-tab" && !attached &&
            ExactObject(request, {"command", "protocol", "sessionId", "tabId", "sequence"})) {
            tabId = incomingTab;
            attached = true;
            if (!Emit(Ack("ack", session, "attached", lastSeq, tabId))) return 2;
        } else if (command == "resize" && attached &&
            ExactObject(request, {"command", "protocol", "sessionId", "tabId", "sequence",
                                  "width", "height", "scale"})) {
            const QJsonValue scale = request.value("scale");
            if (!ValidInt(request.value("width"), 1, 16384) ||
                !ValidInt(request.value("height"), 1, 16384) ||
                !scale.isDouble() || !std::isfinite(scale.toDouble()) ||
                scale.toDouble() < 0.5 || scale.toDouble() > 4.0)
                return 2;
            // Only acknowledge bounded geometry. This does NOT resize a real
            // CEF/Avalonia editor and does not share native OS window handles.
            if (!Emit(Ack("ack", session, "resized", lastSeq, tabId))) return 2;
        } else if (command == "close" && attached &&
                   ExactObject(request, {"command", "protocol", "sessionId", "tabId", "sequence"})) {
            return Emit(Ack("ack", session, "closed", lastSeq, tabId)) ? 0 : 2;
        } else {
            return 2;
        }
    }
    return 2; // no unbounded event loop or communication channel
}
