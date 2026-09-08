#include "GDExtensionMinimalAbi.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>

namespace {

constexpr uint64_t kDotNetDateTimeEpochOffsetTicks = 504911232000000000ULL;
constexpr wchar_t kLogFilePrefix[] = L"code_service_bootstrap_";
constexpr wchar_t kLogFileSuffix[] = L".jsonl";
constexpr char kPatchName[] = "Plugin.gdextension_codeservice_bootstrap_identity_diagnostics_root_v1";
constexpr char kExpectedServiceVersion[] = "0.1.0";
constexpr DWORD kPathCapacity = 4096;
constexpr DWORD kCommandLineCapacity = 12288;
constexpr DWORD kCommandLineFixedOverheadReserve = 512;
constexpr DWORD kRecordCapacity = 16384;
constexpr DWORD kTreeStateMaximumBytes = 256U * 1024U;
constexpr DWORD kJsonReadChunkCapacity = 4096;
constexpr DWORD kJsonMaximumDepth = 64;
constexpr DWORD kJsonKeyCapacity = 64;
constexpr DWORD kDocumentPathMaximumCharacters = 2048;
constexpr DWORD kDocumentPathUtf8Capacity = kDocumentPathMaximumCharacters * 4U;
constexpr DWORD kConfigMaximumBytes = 4096;
constexpr DWORD kServiceVersionCapacity = 129;
constexpr DWORD kMaxProjectRootParentLevels = 16;
constexpr wchar_t kProjectFileName[] = L"project.godot";
constexpr wchar_t kBootstrapConfigFileName[] = L"native_bootstrap.ini";
constexpr wchar_t kTreeStateRelativePath[] = L".godot\\system_explorer\\tree_state.json";
constexpr wchar_t kCanonicalToolRelativePath[] = L".dotnet\\tools\\system-explorer-code.exe";

static_assert(
    kCommandLineCapacity > (kPathCapacity * 2U) + kDocumentPathMaximumCharacters + kCommandLineFixedOverheadReserve,
    "CodeService command-line buffer must cover bounded shim, project root, startup document, and fixed argument overhead."
);

struct CapturedEvent {
    FILETIME fileTime;
    LARGE_INTEGER counter;
    bool ready;
};

struct CodeServiceBootstrapState {
    DWORD pid;
    uint64_t processCreationFileTime100ns;
    uint64_t processStartTimeUtcTicks;
    LARGE_INTEGER qpcFrequency;
    LARGE_INTEGER entryCounter;
    CapturedEvent entryEvent;
    CapturedEvent coreEvent;
    wchar_t logPath[kPathCapacity];
    bool identityReady;
    bool qpcReady;
    bool diagnosticsEnabled;
    bool logPathReady;
    volatile LONG coreLaunchAttempted;
};

struct BootstrapConfig {
    bool enabled;
    bool diagnosticLogging;
    char verifiedServiceVersion[kServiceVersionCapacity];
};

CodeServiceBootstrapState g_state{};

uint64_t FileTimeToUInt64(const FILETIME &value) {
    return (static_cast<uint64_t>(value.dwHighDateTime) << 32U) |
        static_cast<uint64_t>(value.dwLowDateTime);
}

struct WideBuffer {
    wchar_t *data;
    DWORD capacity;
    DWORD length;
    bool ok;
};

void AppendWideChar(WideBuffer &buffer, wchar_t value) {
    if (!buffer.ok || buffer.length + 1U >= buffer.capacity) {
        buffer.ok = false;
        return;
    }

    buffer.data[buffer.length++] = value;
    buffer.data[buffer.length] = L'\0';
}

void AppendWideLiteral(WideBuffer &buffer, const wchar_t *value) {
    if (value == nullptr) {
        buffer.ok = false;
        return;
    }

    while (*value != L'\0' && buffer.ok) {
        AppendWideChar(buffer, *value++);
    }
}

void AppendWideUInt64(WideBuffer &buffer, uint64_t value) {
    wchar_t digits[32];
    DWORD count = 0U;

    do {
        digits[count++] = static_cast<wchar_t>(L'0' + (value % 10ULL));
        value /= 10ULL;
    } while (value != 0ULL && count < static_cast<DWORD>(sizeof(digits) / sizeof(digits[0])));

    while (count > 0U && buffer.ok) {
        AppendWideChar(buffer, digits[--count]);
    }
}

bool CopyWideString(wchar_t *destination, DWORD capacity, const wchar_t *source) {
    if (destination == nullptr || capacity == 0U || source == nullptr) {
        return false;
    }

    WideBuffer buffer{destination, capacity, 0U, true};
    AppendWideLiteral(buffer, source);
    return buffer.ok;
}

bool AppendPathComponent(WideBuffer &buffer, const wchar_t *component) {
    if (!buffer.ok || component == nullptr) {
        return false;
    }

    if (buffer.length > 0U) {
        wchar_t last = buffer.data[buffer.length - 1U];
        if (last != L'\\' && last != L'/') {
            AppendWideChar(buffer, L'\\');
        }
    }

    AppendWideLiteral(buffer, component);
    return buffer.ok;
}

bool EnsureDirectoryExists(const wchar_t *path) {
    if (path == nullptr || path[0] == L'\0') {
        return false;
    }

    DWORD attributes = GetFileAttributesW(path);
    if (attributes != INVALID_FILE_ATTRIBUTES) {
        return (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0U;
    }

    if (CreateDirectoryW(path, nullptr) != FALSE) {
        return true;
    }

    if (GetLastError() != ERROR_ALREADY_EXISTS) {
        return false;
    }

    attributes = GetFileAttributesW(path);
    return attributes != INVALID_FILE_ATTRIBUTES
        && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0U;
}

bool AppendAndEnsureDirectory(WideBuffer &directory, const wchar_t *component) {
    if (!AppendPathComponent(directory, component) || !directory.ok) {
        return false;
    }
    return EnsureDirectoryExists(directory.data);
}

void InitializeLogPath(CodeServiceBootstrapState &state) {
    if (!state.identityReady || !state.diagnosticsEnabled) {
        return;
    }

    PWSTR localApplicationData = nullptr;
    const HRESULT knownFolderResult = SHGetKnownFolderPath(
        FOLDERID_LocalAppData,
        KF_FLAG_DEFAULT,
        nullptr,
        &localApplicationData
    );
    if (FAILED(knownFolderResult) || localApplicationData == nullptr || localApplicationData[0] == L'\0') {
        if (localApplicationData != nullptr) {
            CoTaskMemFree(localApplicationData);
        }
        return;
    }

    wchar_t directoryPath[kPathCapacity]{};
    WideBuffer directory{directoryPath, kPathCapacity, 0U, true};
    AppendWideLiteral(directory, localApplicationData);
    CoTaskMemFree(localApplicationData);
    localApplicationData = nullptr;

    if (!directory.ok) {
        return;
    }
    if (!AppendAndEnsureDirectory(directory, L"SystemExplorer")) {
        return;
    }
    if (!AppendAndEnsureDirectory(directory, L"Diagnostics")) {
        return;
    }
    if (!AppendAndEnsureDirectory(directory, L"CodeServiceBootstrap")) {
        return;
    }

    WideBuffer path{state.logPath, kPathCapacity, 0U, true};
    AppendWideLiteral(path, directory.data);
    AppendWideChar(path, L'\\');
    AppendWideLiteral(path, kLogFilePrefix);
    AppendWideUInt64(path, static_cast<uint64_t>(state.pid));
    AppendWideChar(path, L'_');
    AppendWideUInt64(path, state.processStartTimeUtcTicks);
    AppendWideLiteral(path, kLogFileSuffix);

    state.logPathReady = path.ok;
}

struct CharBuffer {
    char *data;
    DWORD capacity;
    DWORD length;
    bool ok;
};

void AppendChar(CharBuffer &buffer, char value) {
    if (!buffer.ok || buffer.length + 1U >= buffer.capacity) {
        buffer.ok = false;
        return;
    }

    buffer.data[buffer.length++] = value;
    buffer.data[buffer.length] = '\0';
}

void AppendLiteral(CharBuffer &buffer, const char *value) {
    if (value == nullptr) {
        buffer.ok = false;
        return;
    }

    while (*value != '\0' && buffer.ok) {
        AppendChar(buffer, *value++);
    }
}

void AppendUInt64(CharBuffer &buffer, uint64_t value) {
    char digits[32];
    DWORD count = 0U;

    do {
        digits[count++] = static_cast<char>('0' + (value % 10ULL));
        value /= 10ULL;
    } while (value != 0ULL && count < static_cast<DWORD>(sizeof(digits)));

    while (count > 0U && buffer.ok) {
        AppendChar(buffer, digits[--count]);
    }
}

void AppendJsonHexNibble(CharBuffer &buffer, unsigned char value) {
    AppendChar(buffer, static_cast<char>(value < 10U ? '0' + value : 'A' + (value - 10U)));
}

void AppendJsonEscapedUtf8String(
    CharBuffer &buffer,
    const char *value,
    DWORD length
) {
    if (value == nullptr && length != 0U) {
        buffer.ok = false;
        return;
    }

    for (DWORD index = 0U; index < length && buffer.ok; ++index) {
        const unsigned char character = static_cast<unsigned char>(value[index]);
        switch (character) {
            case '"':
                AppendLiteral(buffer, "\\\"");
                break;
            case '\\':
                AppendLiteral(buffer, "\\\\");
                break;
            case '\b':
                AppendLiteral(buffer, "\\b");
                break;
            case '\f':
                AppendLiteral(buffer, "\\f");
                break;
            case '\n':
                AppendLiteral(buffer, "\\n");
                break;
            case '\r':
                AppendLiteral(buffer, "\\r");
                break;
            case '\t':
                AppendLiteral(buffer, "\\t");
                break;
            default:
                if (character < 0x20U) {
                    AppendLiteral(buffer, "\\u00");
                    AppendJsonHexNibble(buffer, static_cast<unsigned char>((character >> 4U) & 0x0fU));
                    AppendJsonHexNibble(buffer, static_cast<unsigned char>(character & 0x0fU));
                } else {
                    AppendChar(buffer, static_cast<char>(character));
                }
                break;
        }
    }
}

void AppendFixedMillisecondsFromMicroseconds(CharBuffer &buffer, uint64_t microseconds) {
    AppendUInt64(buffer, microseconds / 1000ULL);
    AppendChar(buffer, '.');

    uint64_t fractional = microseconds % 1000ULL;
    AppendChar(buffer, static_cast<char>('0' + ((fractional / 100ULL) % 10ULL)));
    AppendChar(buffer, static_cast<char>('0' + ((fractional / 10ULL) % 10ULL)));
    AppendChar(buffer, static_cast<char>('0' + (fractional % 10ULL)));
}

uint64_t QpcDeltaToMicroseconds(const CodeServiceBootstrapState &state, const LARGE_INTEGER &counter) {
    if (!state.qpcReady || state.qpcFrequency.QuadPart <= 0 || counter.QuadPart < state.entryCounter.QuadPart) {
        return 0ULL;
    }

    const uint64_t frequency = static_cast<uint64_t>(state.qpcFrequency.QuadPart);
    const uint64_t delta = static_cast<uint64_t>(counter.QuadPart - state.entryCounter.QuadPart);
    const uint64_t wholeSeconds = delta / frequency;
    const uint64_t remainder = delta % frequency;

    return (wholeSeconds * 1000000ULL) + ((remainder * 1000000ULL) / frequency);
}

bool AppendDiagnosticRecord(
    CodeServiceBootstrapState &state,
    const char *eventName,
    const char *initializationLevel,
    const FILETIME &eventFileTime,
    const LARGE_INTEGER &eventCounter,
    const char *reasonCode,
    DWORD win32Error,
    DWORD servicePid,
    uint64_t serviceStartTimeUtcTicks,
    const char *documentPath,
    DWORD documentPathLength
) {
    if (
        !state.diagnosticsEnabled
        || !state.identityReady
        || !state.qpcReady
        || !state.logPathReady
    ) {
        return true;
    }

    const uint64_t eventFileTime100ns = FileTimeToUInt64(eventFileTime);
    const uint64_t eventTimeUtcTicks = eventFileTime100ns + kDotNetDateTimeEpochOffsetTicks;
    const uint64_t elapsedProcessMicroseconds =
        eventFileTime100ns >= state.processCreationFileTime100ns
        ? (eventFileTime100ns - state.processCreationFileTime100ns) / 10ULL
        : 0ULL;
    const uint64_t elapsedEntryMicroseconds = QpcDeltaToMicroseconds(state, eventCounter);

    char record[kRecordCapacity]{};
    CharBuffer json{record, kRecordCapacity, 0U, true};

    AppendLiteral(json, "{\"schemaVersion\":1,\"experiment\":\"");
    AppendLiteral(json, kPatchName);
    AppendLiteral(json, "\",\"event\":\"");
    AppendLiteral(json, eventName);
    AppendLiteral(json, "\",\"pid\":");
    AppendUInt64(json, static_cast<uint64_t>(state.pid));
    AppendLiteral(json, ",\"threadId\":");
    AppendUInt64(json, static_cast<uint64_t>(GetCurrentThreadId()));
    AppendLiteral(json, ",\"processStartTimeUtcTicks\":");
    AppendUInt64(json, state.processStartTimeUtcTicks);
    AppendLiteral(json, ",\"eventTimeUtcTicks\":");
    AppendUInt64(json, eventTimeUtcTicks);
    AppendLiteral(json, ",\"elapsedFromGodotProcessStartMs\":");
    AppendFixedMillisecondsFromMicroseconds(json, elapsedProcessMicroseconds);
    AppendLiteral(json, ",\"elapsedFromEntryMs\":");
    AppendFixedMillisecondsFromMicroseconds(json, elapsedEntryMicroseconds);
    AppendLiteral(json, ",\"initializationLevel\":\"");
    AppendLiteral(json, initializationLevel);
    AppendChar(json, '"');

    if (reasonCode != nullptr) {
        AppendLiteral(json, ",\"reason\":\"");
        AppendLiteral(json, reasonCode);
        AppendChar(json, '"');
    }

    if (win32Error != 0U) {
        AppendLiteral(json, ",\"win32Error\":");
        AppendUInt64(json, static_cast<uint64_t>(win32Error));
    }

    if (servicePid != 0U) {
        AppendLiteral(json, ",\"servicePid\":");
        AppendUInt64(json, static_cast<uint64_t>(servicePid));
    }

    if (serviceStartTimeUtcTicks != 0ULL) {
        AppendLiteral(json, ",\"serviceStartTimeUtcTicks\":");
        AppendUInt64(json, serviceStartTimeUtcTicks);
    }

    if (documentPath != nullptr) {
        AppendLiteral(json, ",\"documentPath\":\"");
        AppendJsonEscapedUtf8String(json, documentPath, documentPathLength);
        AppendChar(json, '\"');
    }

    AppendLiteral(json, "}\r\n");

    if (!json.ok) {
        return false;
    }

    HANDLE file = CreateFileW(
        state.logPath,
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr
    );

    if (file == INVALID_HANDLE_VALUE) {
        return true;
    }

    DWORD written = 0U;
    WriteFile(file, record, json.length, &written, nullptr);
    CloseHandle(file);
    return true;
}

void AppendEventRecord(
    CodeServiceBootstrapState &state,
    const char *eventName,
    const char *initializationLevel,
    const FILETIME &eventFileTime,
    const LARGE_INTEGER &eventCounter
) {
    AppendDiagnosticRecord(
        state,
        eventName,
        initializationLevel,
        eventFileTime,
        eventCounter,
        nullptr,
        0U,
        0U,
        0ULL,
        nullptr,
        0U
    );
}

void CaptureAndAppendBootstrapRecord(
    CodeServiceBootstrapState &state,
    const char *eventName,
    const char *reasonCode,
    DWORD win32Error,
    DWORD servicePid,
    uint64_t serviceStartTimeUtcTicks
) {
    LARGE_INTEGER counter{};
    FILETIME eventFileTime{};
    if (QueryPerformanceCounter(&counter) == FALSE) {
        return;
    }
    GetSystemTimePreciseAsFileTime(&eventFileTime);

    AppendDiagnosticRecord(
        state,
        eventName,
        "Core",
        eventFileTime,
        counter,
        reasonCode,
        win32Error,
        servicePid,
        serviceStartTimeUtcTicks,
        nullptr,
        0U
    );
}

void InitializeStateFromEntry(
    CodeServiceBootstrapState &state,
    const FILETIME &entryFileTime,
    const LARGE_INTEGER &entryCounter,
    bool entryCounterReady
) {
    state.pid = GetCurrentProcessId();
    state.entryCounter = entryCounter;
    state.entryEvent.fileTime = entryFileTime;
    state.entryEvent.counter = entryCounter;
    state.entryEvent.ready = entryCounterReady;

    LARGE_INTEGER frequency{};
    state.qpcReady = entryCounterReady
        && QueryPerformanceFrequency(&frequency) != FALSE
        && frequency.QuadPart > 0;
    if (state.qpcReady) {
        state.qpcFrequency = frequency;
    }

    FILETIME creation{};
    FILETIME exit{};
    FILETIME kernel{};
    FILETIME user{};
    if (GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user) != FALSE) {
        state.processCreationFileTime100ns = FileTimeToUInt64(creation);
        state.processStartTimeUtcTicks =
            state.processCreationFileTime100ns + kDotNetDateTimeEpochOffsetTicks;
        state.identityReady = true;
    }
}

void CaptureCoreInitializationEvent(CodeServiceBootstrapState &state) {
    LARGE_INTEGER counter{};
    FILETIME eventFileTime{};
    const bool counterReady = QueryPerformanceCounter(&counter) != FALSE;
    GetSystemTimePreciseAsFileTime(&eventFileTime);

    state.coreEvent.fileTime = eventFileTime;
    state.coreEvent.counter = counter;
    state.coreEvent.ready = counterReady;
}

void ActivateDiagnostics(CodeServiceBootstrapState &state) {
    if (state.diagnosticsEnabled) {
        return;
    }

    state.diagnosticsEnabled = true;
    InitializeLogPath(state);

    if (state.entryEvent.ready) {
        AppendEventRecord(
            state,
            "gdextension_entry",
            "Entry",
            state.entryEvent.fileTime,
            state.entryEvent.counter
        );
    }

    if (state.coreEvent.ready) {
        AppendEventRecord(
            state,
            "gdextension_initialize_core",
            "Core",
            state.coreEvent.fileTime,
            state.coreEvent.counter
        );
    }
}

void CaptureAndAppendInitializationEvent(
    CodeServiceBootstrapState &state,
    const char *eventName,
    const char *initializationLevel
) {
    if (!state.diagnosticsEnabled) {
        return;
    }

    LARGE_INTEGER counter{};
    FILETIME eventFileTime{};
    const bool counterReady = QueryPerformanceCounter(&counter) != FALSE;
    GetSystemTimePreciseAsFileTime(&eventFileTime);

    if (!counterReady) {
        return;
    }

    AppendEventRecord(state, eventName, initializationLevel, eventFileTime, counter);
}

bool ResolveCurrentModulePath(wchar_t *modulePath, DWORD capacity, DWORD &win32Error) {
    win32Error = 0U;
    HMODULE module = nullptr;
    if (GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&g_state),
            &module) == FALSE) {
        win32Error = GetLastError();
        return false;
    }

    DWORD length = GetModuleFileNameW(module, modulePath, capacity);
    if (length == 0U) {
        win32Error = GetLastError();
        return false;
    }
    if (length >= capacity - 1U) {
        win32Error = ERROR_INSUFFICIENT_BUFFER;
        return false;
    }

    modulePath[length] = L'\0';
    return true;
}

bool RemoveLastPathComponent(wchar_t *path) {
    if (path == nullptr || path[0] == L'\0') {
        return false;
    }

    DWORD length = 0U;
    while (path[length] != L'\0') {
        ++length;
    }

    while (length > 0U && (path[length - 1U] == L'\\' || path[length - 1U] == L'/')) {
        if (length == 3U && path[1] == L':') {
            return false;
        }
        path[--length] = L'\0';
    }

    while (length > 0U) {
        wchar_t value = path[length - 1U];
        if (value == L'\\' || value == L'/') {
            if (length == 3U && path[1] == L':') {
                path[3] = L'\0';
            } else {
                path[length - 1U] = L'\0';
            }
            return true;
        }
        --length;
    }

    return false;
}

bool IsRegularFile(const wchar_t *path, DWORD &win32Error) {
    win32Error = 0U;
    DWORD attributes = GetFileAttributesW(path);
    if (attributes == INVALID_FILE_ATTRIBUTES) {
        win32Error = GetLastError();
        return false;
    }
    if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0U) {
        win32Error = ERROR_DIRECTORY;
        return false;
    }
    return true;
}

bool ResolveBootstrapConfigPath(
    const wchar_t *modulePath,
    wchar_t *configPath,
    DWORD capacity
) {
    wchar_t bootstrapDirectory[kPathCapacity]{};
    if (!CopyWideString(bootstrapDirectory, kPathCapacity, modulePath)) {
        return false;
    }

    if (!RemoveLastPathComponent(bootstrapDirectory)) {
        return false;
    }
    if (!RemoveLastPathComponent(bootstrapDirectory)) {
        return false;
    }

    WideBuffer path{configPath, capacity, 0U, true};
    AppendWideLiteral(path, bootstrapDirectory);
    AppendPathComponent(path, kBootstrapConfigFileName);
    return path.ok;
}

bool BytesEqualLiteral(const char *value, DWORD length, const char *literal) {
    if (value == nullptr || literal == nullptr) {
        return false;
    }

    DWORD literalLength = 0U;
    while (literal[literalLength] != '\0') {
        ++literalLength;
    }
    if (length != literalLength) {
        return false;
    }

    for (DWORD index = 0U; index < length; ++index) {
        if (value[index] != literal[index]) {
            return false;
        }
    }
    return true;
}

bool LineHasKey(
    const char *line,
    DWORD lineLength,
    const char *key,
    const char *&value,
    DWORD &valueLength
) {
    DWORD keyLength = 0U;
    while (key[keyLength] != '\0') {
        ++keyLength;
    }

    if (lineLength < keyLength + 1U) {
        return false;
    }

    for (DWORD index = 0U; index < keyLength; ++index) {
        if (line[index] != key[index]) {
            return false;
        }
    }

    if (line[keyLength] != '=') {
        return false;
    }

    value = line + keyLength + 1U;
    valueLength = lineLength - keyLength - 1U;
    return true;
}

bool CopyServiceVersion(char *destination, DWORD capacity, const char *value, DWORD length) {
    if (destination == nullptr || capacity == 0U || value == nullptr || length >= capacity) {
        return false;
    }

    for (DWORD index = 0U; index < length; ++index) {
        const unsigned char character = static_cast<unsigned char>(value[index]);
        if (character < 0x21U || character > 0x7eU || character == static_cast<unsigned char>('=')) {
            return false;
        }
        destination[index] = value[index];
    }
    destination[length] = '\0';
    return true;
}

bool ParseBootstrapConfig(const char *data, DWORD length, BootstrapConfig &config) {
    if (data == nullptr || length == 0U || length > kConfigMaximumBytes) {
        return false;
    }

    bool hasFormatVersion = false;
    bool hasEnabled = false;
    bool hasVerifiedServiceVersion = false;
    bool hasDiagnosticLogging = false;

    DWORD offset = 0U;
    while (offset < length) {
        const DWORD lineStart = offset;
        while (offset < length && data[offset] != '\n') {
            ++offset;
        }

        const bool hasLineFeed = offset < length && data[offset] == '\n';
        DWORD lineLength = offset - lineStart;
        if (hasLineFeed) {
            ++offset;
            if (lineLength > 0U && data[lineStart + lineLength - 1U] == '\r') {
                --lineLength;
            }
        }

        if (lineLength == 0U) {
            return false;
        }

        const char *line = data + lineStart;
        const char *value = nullptr;
        DWORD valueLength = 0U;

        if (LineHasKey(line, lineLength, "format_version", value, valueLength)) {
            if (hasFormatVersion || !BytesEqualLiteral(value, valueLength, "1")) {
                return false;
            }
            hasFormatVersion = true;
            continue;
        }

        if (LineHasKey(line, lineLength, "enabled", value, valueLength)) {
            if (hasEnabled) {
                return false;
            }
            if (BytesEqualLiteral(value, valueLength, "true")) {
                config.enabled = true;
            } else if (BytesEqualLiteral(value, valueLength, "false")) {
                config.enabled = false;
            } else {
                return false;
            }
            hasEnabled = true;
            continue;
        }

        if (LineHasKey(line, lineLength, "verified_service_version", value, valueLength)) {
            if (hasVerifiedServiceVersion) {
                return false;
            }
            if (valueLength == 0U) {
                config.verifiedServiceVersion[0] = '\0';
            } else if (!CopyServiceVersion(
                    config.verifiedServiceVersion,
                    kServiceVersionCapacity,
                    value,
                    valueLength)) {
                return false;
            }
            hasVerifiedServiceVersion = true;
            continue;
        }

        if (LineHasKey(line, lineLength, "diagnostic_logging", value, valueLength)) {
            if (hasDiagnosticLogging) {
                return false;
            }
            if (BytesEqualLiteral(value, valueLength, "true")) {
                config.diagnosticLogging = true;
            } else if (BytesEqualLiteral(value, valueLength, "false")) {
                config.diagnosticLogging = false;
            } else {
                return false;
            }
            hasDiagnosticLogging = true;
            continue;
        }

        return false;
    }

    return hasFormatVersion
        && hasEnabled
        && hasVerifiedServiceVersion
        && hasDiagnosticLogging;
}

bool ReadBootstrapConfig(const wchar_t *configPath, BootstrapConfig &config) {
    HANDLE file = CreateFileW(
        configPath,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr
    );
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }

    LARGE_INTEGER size{};
    if (
        GetFileSizeEx(file, &size) == FALSE
        || size.QuadPart <= 0
        || static_cast<uint64_t>(size.QuadPart) > static_cast<uint64_t>(kConfigMaximumBytes)
    ) {
        CloseHandle(file);
        return false;
    }

    char data[kConfigMaximumBytes + 1U]{};
    DWORD bytesRead = 0U;
    const DWORD expectedBytes = static_cast<DWORD>(size.QuadPart);
    BOOL readSucceeded = ReadFile(file, data, expectedBytes, &bytesRead, nullptr);
    CloseHandle(file);

    if (readSucceeded == FALSE || bytesRead != expectedBytes) {
        return false;
    }

    data[bytesRead] = '\0';
    return ParseBootstrapConfig(data, bytesRead, config);
}

bool ExactAsciiStringEquals(const char *left, const char *right) {
    if (left == nullptr || right == nullptr) {
        return false;
    }

    DWORD index = 0U;
    while (left[index] != '\0' && right[index] != '\0') {
        if (left[index] != right[index]) {
            return false;
        }
        ++index;
    }

    return left[index] == '\0' && right[index] == '\0';
}


struct JsonStream {
    HANDLE file;
    unsigned char buffer[kJsonReadChunkCapacity];
    DWORD offset;
    DWORD count;
    uint64_t remaining;
    bool ioFailed;
    DWORD win32Error;
};

struct JsonStringCapture {
    char *data;
    DWORD capacity;
    DWORD length;
    bool overflow;
};

enum class LastScriptValueKind : unsigned char {
    Missing,
    NullValue,
    StringValue,
    InvalidType
};

struct ParsedTreeState {
    bool hasFormatVersion;
    bool formatVersionSupported;
    bool hasLastScript;
    LastScriptValueKind lastScriptKind;
    char lastScriptUtf8[kDocumentPathUtf8Capacity];
    DWORD lastScriptUtf8Length;
    bool lastScriptOverflow;
};

bool EnsureJsonByte(JsonStream &stream) {
    if (stream.offset < stream.count) {
        return true;
    }

    if (stream.remaining == 0ULL) {
        return false;
    }

    const uint64_t requested64 = stream.remaining < static_cast<uint64_t>(kJsonReadChunkCapacity)
        ? stream.remaining
        : static_cast<uint64_t>(kJsonReadChunkCapacity);
    const DWORD requested = static_cast<DWORD>(requested64);
    DWORD bytesRead = 0U;
    if (ReadFile(stream.file, stream.buffer, requested, &bytesRead, nullptr) == FALSE) {
        stream.ioFailed = true;
        stream.win32Error = GetLastError();
        return false;
    }
    if (bytesRead == 0U || bytesRead > requested) {
        stream.ioFailed = true;
        stream.win32Error = ERROR_HANDLE_EOF;
        return false;
    }

    stream.offset = 0U;
    stream.count = bytesRead;
    stream.remaining -= static_cast<uint64_t>(bytesRead);
    return true;
}

bool PeekJsonByte(JsonStream &stream, unsigned char &value) {
    if (!EnsureJsonByte(stream)) {
        return false;
    }
    value = stream.buffer[stream.offset];
    return true;
}

bool ReadJsonByte(JsonStream &stream, unsigned char &value) {
    if (!PeekJsonByte(stream, value)) {
        return false;
    }
    ++stream.offset;
    return true;
}

bool IsJsonWhitespace(unsigned char value) {
    return value == 0x20U || value == 0x09U || value == 0x0aU || value == 0x0dU;
}

void SkipJsonWhitespace(JsonStream &stream) {
    unsigned char value = 0U;
    while (PeekJsonByte(stream, value) && IsJsonWhitespace(value)) {
        ++stream.offset;
    }
}

void CaptureDecodedByte(JsonStringCapture &capture, unsigned char value) {
    if (capture.data != nullptr) {
        if (capture.length < capture.capacity) {
            capture.data[capture.length] = static_cast<char>(value);
        } else {
            capture.overflow = true;
        }
    }
    ++capture.length;
}

void CaptureUtf8CodePoint(JsonStringCapture &capture, uint32_t codePoint) {
    if (codePoint <= 0x7fU) {
        CaptureDecodedByte(capture, static_cast<unsigned char>(codePoint));
    } else if (codePoint <= 0x7ffU) {
        CaptureDecodedByte(capture, static_cast<unsigned char>(0xc0U | (codePoint >> 6U)));
        CaptureDecodedByte(capture, static_cast<unsigned char>(0x80U | (codePoint & 0x3fU)));
    } else if (codePoint <= 0xffffU) {
        CaptureDecodedByte(capture, static_cast<unsigned char>(0xe0U | (codePoint >> 12U)));
        CaptureDecodedByte(capture, static_cast<unsigned char>(0x80U | ((codePoint >> 6U) & 0x3fU)));
        CaptureDecodedByte(capture, static_cast<unsigned char>(0x80U | (codePoint & 0x3fU)));
    } else {
        CaptureDecodedByte(capture, static_cast<unsigned char>(0xf0U | (codePoint >> 18U)));
        CaptureDecodedByte(capture, static_cast<unsigned char>(0x80U | ((codePoint >> 12U) & 0x3fU)));
        CaptureDecodedByte(capture, static_cast<unsigned char>(0x80U | ((codePoint >> 6U) & 0x3fU)));
        CaptureDecodedByte(capture, static_cast<unsigned char>(0x80U | (codePoint & 0x3fU)));
    }
}

bool ReadHexDigit(JsonStream &stream, uint32_t &value) {
    unsigned char character = 0U;
    if (!ReadJsonByte(stream, character)) {
        return false;
    }

    if (character >= '0' && character <= '9') {
        value = static_cast<uint32_t>(character - '0');
        return true;
    }
    if (character >= 'a' && character <= 'f') {
        value = 10U + static_cast<uint32_t>(character - 'a');
        return true;
    }
    if (character >= 'A' && character <= 'F') {
        value = 10U + static_cast<uint32_t>(character - 'A');
        return true;
    }
    return false;
}

bool ReadUnicodeEscape(JsonStream &stream, uint32_t &value) {
    value = 0U;
    for (DWORD index = 0U; index < 4U; ++index) {
        uint32_t nibble = 0U;
        if (!ReadHexDigit(stream, nibble)) {
            return false;
        }
        value = (value << 4U) | nibble;
    }
    return true;
}

bool ParseJsonString(JsonStream &stream, JsonStringCapture &capture) {
    unsigned char character = 0U;
    if (!ReadJsonByte(stream, character) || character != '"') {
        return false;
    }

    while (ReadJsonByte(stream, character)) {
        if (character == '"') {
            return true;
        }
        if (character < 0x20U) {
            return false;
        }

        if (character == '\\') {
            unsigned char escape = 0U;
            if (!ReadJsonByte(stream, escape)) {
                return false;
            }

            switch (escape) {
                case '"': CaptureDecodedByte(capture, '"'); break;
                case '\\': CaptureDecodedByte(capture, '\\'); break;
                case '/': CaptureDecodedByte(capture, '/'); break;
                case 'b': CaptureDecodedByte(capture, 0x08U); break;
                case 'f': CaptureDecodedByte(capture, 0x0cU); break;
                case 'n': CaptureDecodedByte(capture, 0x0aU); break;
                case 'r': CaptureDecodedByte(capture, 0x0dU); break;
                case 't': CaptureDecodedByte(capture, 0x09U); break;
                case 'u': {
                    uint32_t first = 0U;
                    if (!ReadUnicodeEscape(stream, first)) {
                        return false;
                    }

                    uint32_t codePoint = first;
                    if (first >= 0xd800U && first <= 0xdbffU) {
                        unsigned char slash = 0U;
                        unsigned char marker = 0U;
                        if (
                            !ReadJsonByte(stream, slash)
                            || slash != '\\'
                            || !ReadJsonByte(stream, marker)
                            || marker != 'u'
                        ) {
                            return false;
                        }

                        uint32_t second = 0U;
                        if (!ReadUnicodeEscape(stream, second) || second < 0xdc00U || second > 0xdfffU) {
                            return false;
                        }
                        codePoint = 0x10000U + ((first - 0xd800U) << 10U) + (second - 0xdc00U);
                    } else if (first >= 0xdc00U && first <= 0xdfffU) {
                        return false;
                    }

                    CaptureUtf8CodePoint(capture, codePoint);
                    break;
                }
                default:
                    return false;
            }
            continue;
        }

        if (character < 0x80U) {
            CaptureDecodedByte(capture, character);
            continue;
        }

        DWORD continuationCount = 0U;
        uint32_t codePoint = 0U;
        uint32_t minimumCodePoint = 0U;
        if (character >= 0xc2U && character <= 0xdfU) {
            continuationCount = 1U;
            codePoint = static_cast<uint32_t>(character & 0x1fU);
            minimumCodePoint = 0x80U;
        } else if (character >= 0xe0U && character <= 0xefU) {
            continuationCount = 2U;
            codePoint = static_cast<uint32_t>(character & 0x0fU);
            minimumCodePoint = 0x800U;
        } else if (character >= 0xf0U && character <= 0xf4U) {
            continuationCount = 3U;
            codePoint = static_cast<uint32_t>(character & 0x07U);
            minimumCodePoint = 0x10000U;
        } else {
            return false;
        }

        for (DWORD index = 0U; index < continuationCount; ++index) {
            unsigned char continuation = 0U;
            if (!ReadJsonByte(stream, continuation) || (continuation & 0xc0U) != 0x80U) {
                return false;
            }
            codePoint = (codePoint << 6U) | static_cast<uint32_t>(continuation & 0x3fU);
        }

        if (
            codePoint < minimumCodePoint
            || codePoint > 0x10ffffU
            || (codePoint >= 0xd800U && codePoint <= 0xdfffU)
        ) {
            return false;
        }

        CaptureUtf8CodePoint(capture, codePoint);
    }

    return false;
}

bool ParseJsonLiteral(JsonStream &stream, const char *literal) {
    if (literal == nullptr) {
        return false;
    }

    for (DWORD index = 0U; literal[index] != '\0'; ++index) {
        unsigned char value = 0U;
        if (!ReadJsonByte(stream, value) || value != static_cast<unsigned char>(literal[index])) {
            return false;
        }
    }
    return true;
}

bool IsJsonDigit(unsigned char value) {
    return value >= '0' && value <= '9';
}

bool ParseJsonNumber(JsonStream &stream, JsonStringCapture *capture) {
    unsigned char value = 0U;
    if (!PeekJsonByte(stream, value)) {
        return false;
    }

    if (value == '-') {
        ReadJsonByte(stream, value);
        if (capture != nullptr) {
            CaptureDecodedByte(*capture, '-');
        }
        if (!PeekJsonByte(stream, value)) {
            return false;
        }
    }

    if (value == '0') {
        ReadJsonByte(stream, value);
        if (capture != nullptr) {
            CaptureDecodedByte(*capture, '0');
        }
        unsigned char next = 0U;
        if (PeekJsonByte(stream, next) && IsJsonDigit(next)) {
            return false;
        }
    } else if (value >= '1' && value <= '9') {
        do {
            ReadJsonByte(stream, value);
            if (capture != nullptr) {
                CaptureDecodedByte(*capture, value);
            }
        } while (PeekJsonByte(stream, value) && IsJsonDigit(value));
    } else {
        return false;
    }

    if (PeekJsonByte(stream, value) && value == '.') {
        ReadJsonByte(stream, value);
        if (capture != nullptr) {
            CaptureDecodedByte(*capture, '.');
        }
        if (!PeekJsonByte(stream, value) || !IsJsonDigit(value)) {
            return false;
        }
        do {
            ReadJsonByte(stream, value);
            if (capture != nullptr) {
                CaptureDecodedByte(*capture, value);
            }
        } while (PeekJsonByte(stream, value) && IsJsonDigit(value));
    }

    if (PeekJsonByte(stream, value) && (value == 'e' || value == 'E')) {
        ReadJsonByte(stream, value);
        if (capture != nullptr) {
            CaptureDecodedByte(*capture, value);
        }
        if (PeekJsonByte(stream, value) && (value == '+' || value == '-')) {
            ReadJsonByte(stream, value);
            if (capture != nullptr) {
                CaptureDecodedByte(*capture, value);
            }
        }
        if (!PeekJsonByte(stream, value) || !IsJsonDigit(value)) {
            return false;
        }
        do {
            ReadJsonByte(stream, value);
            if (capture != nullptr) {
                CaptureDecodedByte(*capture, value);
            }
        } while (PeekJsonByte(stream, value) && IsJsonDigit(value));
    }

    return true;
}

bool SkipJsonValue(JsonStream &stream, DWORD depth);

bool SkipJsonObject(JsonStream &stream, DWORD depth) {
    if (depth >= kJsonMaximumDepth) {
        return false;
    }

    unsigned char value = 0U;
    if (!ReadJsonByte(stream, value) || value != '{') {
        return false;
    }

    SkipJsonWhitespace(stream);
    if (PeekJsonByte(stream, value) && value == '}') {
        ++stream.offset;
        return true;
    }

    while (true) {
        JsonStringCapture ignored{nullptr, 0U, 0U, false};
        if (!ParseJsonString(stream, ignored)) {
            return false;
        }
        SkipJsonWhitespace(stream);
        if (!ReadJsonByte(stream, value) || value != ':') {
            return false;
        }
        if (!SkipJsonValue(stream, depth + 1U)) {
            return false;
        }
        SkipJsonWhitespace(stream);
        if (!ReadJsonByte(stream, value)) {
            return false;
        }
        if (value == '}') {
            return true;
        }
        if (value != ',') {
            return false;
        }
        SkipJsonWhitespace(stream);
    }
}

bool SkipJsonArray(JsonStream &stream, DWORD depth) {
    if (depth >= kJsonMaximumDepth) {
        return false;
    }

    unsigned char value = 0U;
    if (!ReadJsonByte(stream, value) || value != '[') {
        return false;
    }

    SkipJsonWhitespace(stream);
    if (PeekJsonByte(stream, value) && value == ']') {
        ++stream.offset;
        return true;
    }

    while (true) {
        if (!SkipJsonValue(stream, depth + 1U)) {
            return false;
        }
        SkipJsonWhitespace(stream);
        if (!ReadJsonByte(stream, value)) {
            return false;
        }
        if (value == ']') {
            return true;
        }
        if (value != ',') {
            return false;
        }
        SkipJsonWhitespace(stream);
    }
}

bool SkipJsonValue(JsonStream &stream, DWORD depth) {
    if (depth > kJsonMaximumDepth) {
        return false;
    }

    SkipJsonWhitespace(stream);
    unsigned char value = 0U;
    if (!PeekJsonByte(stream, value)) {
        return false;
    }

    if (value == '{') {
        return SkipJsonObject(stream, depth);
    }
    if (value == '[') {
        return SkipJsonArray(stream, depth);
    }
    if (value == '"') {
        JsonStringCapture ignored{nullptr, 0U, 0U, false};
        return ParseJsonString(stream, ignored);
    }
    if (value == 't') {
        return ParseJsonLiteral(stream, "true");
    }
    if (value == 'f') {
        return ParseJsonLiteral(stream, "false");
    }
    if (value == 'n') {
        return ParseJsonLiteral(stream, "null");
    }
    if (value == '-' || IsJsonDigit(value)) {
        return ParseJsonNumber(stream, nullptr);
    }
    return false;
}

bool ParseFormatVersionValue(JsonStream &stream, bool &supported) {
    supported = false;
    SkipJsonWhitespace(stream);

    unsigned char value = 0U;
    if (!PeekJsonByte(stream, value)) {
        return false;
    }

    if (value == '-' || IsJsonDigit(value)) {
        char token[32]{};
        JsonStringCapture capture{token, static_cast<DWORD>(sizeof(token)), 0U, false};
        if (!ParseJsonNumber(stream, &capture)) {
            return false;
        }
        supported = !capture.overflow
            && capture.length == 1U
            && token[0] == '1';
        return true;
    }

    return SkipJsonValue(stream, 1U);
}

bool ParseLastScriptValue(JsonStream &stream, ParsedTreeState &state) {
    SkipJsonWhitespace(stream);
    unsigned char value = 0U;
    if (!PeekJsonByte(stream, value)) {
        return false;
    }

    if (value == '"') {
        JsonStringCapture capture{
            state.lastScriptUtf8,
            kDocumentPathUtf8Capacity,
            0U,
            false
        };
        if (!ParseJsonString(stream, capture)) {
            return false;
        }
        state.lastScriptKind = LastScriptValueKind::StringValue;
        state.lastScriptUtf8Length = capture.length;
        state.lastScriptOverflow = capture.overflow;
        return true;
    }

    if (value == 'n') {
        if (!ParseJsonLiteral(stream, "null")) {
            return false;
        }
        state.lastScriptKind = LastScriptValueKind::NullValue;
        return true;
    }

    state.lastScriptKind = LastScriptValueKind::InvalidType;
    return SkipJsonValue(stream, 1U);
}

bool ParseTreeStateJson(HANDLE file, uint64_t fileSize, ParsedTreeState &state, DWORD &readError) {
    readError = 0U;
    JsonStream stream{file, {}, 0U, 0U, fileSize, false, 0U};

    unsigned char value = 0U;
    if (PeekJsonByte(stream, value) && value == 0xefU) {
        unsigned char bom0 = 0U;
        unsigned char bom1 = 0U;
        unsigned char bom2 = 0U;
        if (
            !ReadJsonByte(stream, bom0)
            || !ReadJsonByte(stream, bom1)
            || !ReadJsonByte(stream, bom2)
            || bom0 != 0xefU
            || bom1 != 0xbbU
            || bom2 != 0xbfU
        ) {
            readError = stream.ioFailed ? stream.win32Error : 0U;
            return false;
        }
    }

    SkipJsonWhitespace(stream);
    if (!ReadJsonByte(stream, value) || value != '{') {
        readError = stream.ioFailed ? stream.win32Error : 0U;
        return false;
    }

    SkipJsonWhitespace(stream);
    if (PeekJsonByte(stream, value) && value == '}') {
        ++stream.offset;
    } else {
        while (true) {
            char key[kJsonKeyCapacity]{};
            JsonStringCapture keyCapture{key, kJsonKeyCapacity, 0U, false};
            if (!ParseJsonString(stream, keyCapture)) {
                readError = stream.ioFailed ? stream.win32Error : 0U;
                return false;
            }

            SkipJsonWhitespace(stream);
            if (!ReadJsonByte(stream, value) || value != ':') {
                readError = stream.ioFailed ? stream.win32Error : 0U;
                return false;
            }

            const bool isFormatVersion = !keyCapture.overflow
                && BytesEqualLiteral(key, keyCapture.length, "format_version");
            const bool isLastScript = !keyCapture.overflow
                && BytesEqualLiteral(key, keyCapture.length, "last_script");

            if (isFormatVersion) {
                if (state.hasFormatVersion) {
                    return false;
                }
                state.hasFormatVersion = true;
                if (!ParseFormatVersionValue(stream, state.formatVersionSupported)) {
                    readError = stream.ioFailed ? stream.win32Error : 0U;
                    return false;
                }
            } else if (isLastScript) {
                if (state.hasLastScript) {
                    return false;
                }
                state.hasLastScript = true;
                if (!ParseLastScriptValue(stream, state)) {
                    readError = stream.ioFailed ? stream.win32Error : 0U;
                    return false;
                }
            } else if (!SkipJsonValue(stream, 1U)) {
                readError = stream.ioFailed ? stream.win32Error : 0U;
                return false;
            }

            SkipJsonWhitespace(stream);
            if (!ReadJsonByte(stream, value)) {
                readError = stream.ioFailed ? stream.win32Error : 0U;
                return false;
            }
            if (value == '}') {
                break;
            }
            if (value != ',') {
                return false;
            }
            SkipJsonWhitespace(stream);
        }
    }

    SkipJsonWhitespace(stream);
    if (PeekJsonByte(stream, value)) {
        return false;
    }
    if (stream.ioFailed) {
        readError = stream.win32Error;
        return false;
    }
    return true;
}

bool IsAsciiCaseInsensitiveCsSuffix(const wchar_t *path, DWORD length) {
    if (path == nullptr || length < 3U || path[length - 3U] != L'.') {
        return false;
    }

    const wchar_t c = path[length - 2U];
    const wchar_t s = path[length - 1U];
    return (c == L'c' || c == L'C') && (s == L's' || s == L'S');
}

bool ValidateDocumentWirePath(
    const char *utf8Path,
    DWORD utf8Length,
    wchar_t *widePath,
    DWORD wideCapacity,
    DWORD &wideLength
) {
    wideLength = 0U;
    if (utf8Path == nullptr || utf8Length == 0U || utf8Length > kDocumentPathUtf8Capacity) {
        return false;
    }

    const int convertedLength = MultiByteToWideChar(
        CP_UTF8,
        MB_ERR_INVALID_CHARS,
        utf8Path,
        static_cast<int>(utf8Length),
        nullptr,
        0
    );
    if (
        convertedLength <= 0
        || convertedLength > static_cast<int>(kDocumentPathMaximumCharacters)
        || convertedLength + 1 > static_cast<int>(wideCapacity)
    ) {
        return false;
    }

    const int written = MultiByteToWideChar(
        CP_UTF8,
        MB_ERR_INVALID_CHARS,
        utf8Path,
        static_cast<int>(utf8Length),
        widePath,
        convertedLength
    );
    if (written != convertedLength) {
        return false;
    }

    widePath[convertedLength] = L'\0';
    wideLength = static_cast<DWORD>(convertedLength);

    if (!IsAsciiCaseInsensitiveCsSuffix(widePath, wideLength)) {
        return false;
    }
    if (widePath[0] == L'/' || widePath[wideLength - 1U] == L'/') {
        return false;
    }

    DWORD segmentStart = 0U;
    for (DWORD index = 0U; index <= wideLength; ++index) {
        const bool atEnd = index == wideLength;
        const wchar_t character = atEnd ? L'/' : widePath[index];

        if (!atEnd && (character == L'\0' || character == L'\\' || character == L':')) {
            return false;
        }

        if (character != L'/') {
            continue;
        }

        const DWORD segmentLength = index - segmentStart;
        if (segmentLength == 0U) {
            return false;
        }
        if (segmentLength == 1U && widePath[segmentStart] == L'.') {
            return false;
        }
        if (
            segmentLength == 2U
            && widePath[segmentStart] == L'.'
            && widePath[segmentStart + 1U] == L'.'
        ) {
            return false;
        }
        segmentStart = index + 1U;
    }

    return true;
}

bool BuildDocumentFileSystemPath(
    const wchar_t *projectRoot,
    const wchar_t *documentPath,
    DWORD documentPathLength,
    wchar_t *targetPath,
    DWORD capacity
) {
    WideBuffer path{targetPath, capacity, 0U, true};
    AppendWideLiteral(path, projectRoot);
    if (path.length > 0U && path.data[path.length - 1U] != L'\\' && path.data[path.length - 1U] != L'/') {
        AppendWideChar(path, L'\\');
    }

    for (DWORD index = 0U; index < documentPathLength && path.ok; ++index) {
        AppendWideChar(path, documentPath[index] == L'/' ? L'\\' : documentPath[index]);
    }
    return path.ok;
}

void CaptureAndAppendStartupDocumentRecord(
    CodeServiceBootstrapState &state,
    const char *eventName,
    const char *reasonCode,
    DWORD win32Error,
    const char *documentPath,
    DWORD documentPathLength
) {
    LARGE_INTEGER counter{};
    FILETIME eventFileTime{};
    if (QueryPerformanceCounter(&counter) == FALSE) {
        return;
    }
    GetSystemTimePreciseAsFileTime(&eventFileTime);

    const bool serialized = AppendDiagnosticRecord(
        state,
        eventName,
        "Core",
        eventFileTime,
        counter,
        reasonCode,
        win32Error,
        0U,
        0ULL,
        documentPath,
        documentPathLength
    );

    if (!serialized && documentPath != nullptr) {
        AppendDiagnosticRecord(
            state,
            "startup_document_path_discovery_failed",
            "Core",
            eventFileTime,
            counter,
            "diagnostic_document_path_overflow",
            0U,
            0U,
            0ULL,
            nullptr,
            0U
        );
    }
}

bool DiscoverStartupDocumentPath(
    CodeServiceBootstrapState &state,
    const wchar_t *projectRoot,
    wchar_t *documentPath,
    DWORD documentPathCapacity
) {
    if (documentPath == nullptr || documentPathCapacity == 0U) {
        return false;
    }
    documentPath[0] = L'\0';
    CaptureAndAppendStartupDocumentRecord(
        state,
        "startup_document_path_discovery_started",
        nullptr,
        0U,
        nullptr,
        0U
    );

    wchar_t treeStatePath[kPathCapacity]{};
    WideBuffer treeState{treeStatePath, kPathCapacity, 0U, true};
    AppendWideLiteral(treeState, projectRoot);
    AppendPathComponent(treeState, kTreeStateRelativePath);
    if (!treeState.ok) {
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_discovery_failed",
            "tree_state_path_overflow",
            ERROR_INSUFFICIENT_BUFFER,
            nullptr,
            0U
        );
        return false;
    }

    HANDLE file = CreateFileW(
        treeStatePath,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr
    );
    if (file == INVALID_HANDLE_VALUE) {
        const DWORD error = GetLastError();
        const bool missing = error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND;
        CaptureAndAppendStartupDocumentRecord(
            state,
            missing ? "startup_document_path_missing" : "startup_document_path_discovery_failed",
            missing ? "tree_state_missing" : "tree_state_open_failed",
            error,
            nullptr,
            0U
        );
        return false;
    }

    LARGE_INTEGER size{};
    if (GetFileSizeEx(file, &size) == FALSE) {
        const DWORD error = GetLastError();
        CloseHandle(file);
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_discovery_failed",
            "tree_state_size_failed",
            error,
            nullptr,
            0U
        );
        return false;
    }

    if (size.QuadPart < 0 || static_cast<uint64_t>(size.QuadPart) > static_cast<uint64_t>(kTreeStateMaximumBytes)) {
        CloseHandle(file);
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_discovery_failed",
            "tree_state_too_large",
            0U,
            nullptr,
            0U
        );
        return false;
    }

    ParsedTreeState parsed{};
    parsed.lastScriptKind = LastScriptValueKind::Missing;
    DWORD readError = 0U;
    const bool parsedSuccessfully = ParseTreeStateJson(
        file,
        static_cast<uint64_t>(size.QuadPart),
        parsed,
        readError
    );
    CloseHandle(file);

    if (!parsedSuccessfully) {
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_discovery_failed",
            readError != 0U ? "tree_state_read_failed" : "tree_state_malformed",
            readError,
            nullptr,
            0U
        );
        return false;
    }

    if (!parsed.hasFormatVersion) {
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_discovery_failed",
            "tree_state_format_missing",
            0U,
            nullptr,
            0U
        );
        return false;
    }
    if (!parsed.formatVersionSupported) {
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_discovery_failed",
            "tree_state_format_unsupported",
            0U,
            nullptr,
            0U
        );
        return false;
    }

    if (!parsed.hasLastScript || parsed.lastScriptKind == LastScriptValueKind::Missing) {
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_missing",
            "last_script_missing",
            0U,
            nullptr,
            0U
        );
        return false;
    }
    if (parsed.lastScriptKind == LastScriptValueKind::NullValue) {
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_missing",
            "last_script_null",
            0U,
            nullptr,
            0U
        );
        return false;
    }
    if (parsed.lastScriptKind == LastScriptValueKind::InvalidType) {
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_invalid",
            "last_script_type_invalid",
            0U,
            nullptr,
            0U
        );
        return false;
    }
    if (parsed.lastScriptOverflow) {
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_invalid",
            "last_script_invalid_wire_path",
            0U,
            nullptr,
            0U
        );
        return false;
    }

    wchar_t validatedDocumentPath[kDocumentPathMaximumCharacters + 1U]{};
    DWORD documentPathLength = 0U;
    if (!ValidateDocumentWirePath(
            parsed.lastScriptUtf8,
            parsed.lastScriptUtf8Length,
            validatedDocumentPath,
            kDocumentPathMaximumCharacters + 1U,
            documentPathLength)) {
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_invalid",
            "last_script_invalid_wire_path",
            0U,
            nullptr,
            0U
        );
        return false;
    }

    wchar_t targetPath[kPathCapacity]{};
    if (!BuildDocumentFileSystemPath(
            projectRoot,
            validatedDocumentPath,
            documentPathLength,
            targetPath,
            kPathCapacity)) {
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_invalid",
            "document_path_overflow",
            ERROR_INSUFFICIENT_BUFFER,
            nullptr,
            0U
        );
        return false;
    }

    DWORD targetError = 0U;
    if (!IsRegularFile(targetPath, targetError)) {
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_invalid",
            "startup_document_target_missing",
            targetError,
            nullptr,
            0U
        );
        return false;
    }

    if (!CopyWideString(documentPath, documentPathCapacity, validatedDocumentPath)) {
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_path_invalid",
            "document_path_overflow",
            ERROR_INSUFFICIENT_BUFFER,
            nullptr,
            0U
        );
        documentPath[0] = L'\0';
        return false;
    }

    CaptureAndAppendStartupDocumentRecord(
        state,
        "startup_document_path_discovered",
        nullptr,
        0U,
        parsed.lastScriptUtf8,
        parsed.lastScriptUtf8Length
    );
    return true;
}

bool ResolveProjectRoot(
    const wchar_t *modulePath,
    wchar_t *projectRoot,
    DWORD capacity,
    DWORD &win32Error
) {
    win32Error = 0U;
    if (!CopyWideString(projectRoot, capacity, modulePath)) {
        win32Error = ERROR_INSUFFICIENT_BUFFER;
        return false;
    }

    if (!RemoveLastPathComponent(projectRoot)) {
        win32Error = ERROR_PATH_NOT_FOUND;
        return false;
    }

    for (DWORD level = 0U; level < kMaxProjectRootParentLevels; ++level) {
        wchar_t projectFile[kPathCapacity]{};
        WideBuffer candidate{projectFile, kPathCapacity, 0U, true};
        AppendWideLiteral(candidate, projectRoot);
        AppendPathComponent(candidate, kProjectFileName);
        if (!candidate.ok) {
            win32Error = ERROR_INSUFFICIENT_BUFFER;
            return false;
        }

        DWORD fileError = 0U;
        if (IsRegularFile(candidate.data, fileError)) {
            return true;
        }

        if (!RemoveLastPathComponent(projectRoot)) {
            break;
        }
    }

    win32Error = ERROR_FILE_NOT_FOUND;
    return false;
}

bool ResolveCanonicalServiceShim(
    wchar_t *shimPath,
    DWORD capacity,
    DWORD &win32Error
) {
    win32Error = 0U;
    wchar_t userProfile[kPathCapacity]{};
    DWORD length = GetEnvironmentVariableW(L"USERPROFILE", userProfile, kPathCapacity);
    if (length == 0U) {
        win32Error = GetLastError();
        return false;
    }
    if (length >= kPathCapacity) {
        win32Error = ERROR_INSUFFICIENT_BUFFER;
        return false;
    }

    WideBuffer path{shimPath, capacity, 0U, true};
    AppendWideLiteral(path, userProfile);
    AppendPathComponent(path, kCanonicalToolRelativePath);
    if (!path.ok) {
        win32Error = ERROR_INSUFFICIENT_BUFFER;
        return false;
    }

    DWORD fileError = 0U;
    if (!IsRegularFile(shimPath, fileError)) {
        win32Error = fileError;
        return false;
    }

    return true;
}

void AppendWindowsQuotedArgument(WideBuffer &buffer, const wchar_t *value) {
    if (!buffer.ok || value == nullptr) {
        buffer.ok = false;
        return;
    }

    AppendWideChar(buffer, L'"');
    DWORD backslashCount = 0U;

    for (const wchar_t *cursor = value; *cursor != L'\0' && buffer.ok; ++cursor) {
        if (*cursor == L'\\') {
            ++backslashCount;
            continue;
        }

        if (*cursor == L'"') {
            for (DWORD index = 0U; index < (backslashCount * 2U) + 1U; ++index) {
                AppendWideChar(buffer, L'\\');
            }
            AppendWideChar(buffer, L'"');
            backslashCount = 0U;
            continue;
        }

        for (DWORD index = 0U; index < backslashCount; ++index) {
            AppendWideChar(buffer, L'\\');
        }
        backslashCount = 0U;
        AppendWideChar(buffer, *cursor);
    }

    for (DWORD index = 0U; index < backslashCount * 2U; ++index) {
        AppendWideChar(buffer, L'\\');
    }
    AppendWideChar(buffer, L'"');
}

bool BuildServiceCommandLine(
    const wchar_t *shimPath,
    DWORD godotPid,
    uint64_t godotStartTimeUtcTicks,
    const wchar_t *projectRoot,
    const wchar_t *startupDocumentPath,
    bool diagnosticLogging,
    wchar_t *commandLine,
    DWORD capacity
) {
    WideBuffer buffer{commandLine, capacity, 0U, true};
    AppendWindowsQuotedArgument(buffer, shimPath);
    AppendWideLiteral(buffer, L" server --godot-pid ");
    AppendWideUInt64(buffer, static_cast<uint64_t>(godotPid));
    AppendWideLiteral(buffer, L" --godot-start-time-utc-ticks ");
    AppendWideUInt64(buffer, godotStartTimeUtcTicks);
    AppendWideLiteral(buffer, L" --project-root ");
    AppendWindowsQuotedArgument(buffer, projectRoot);
    if (startupDocumentPath != nullptr && startupDocumentPath[0] != L'\0') {
        AppendWideLiteral(buffer, L" --startup-document ");
        AppendWindowsQuotedArgument(buffer, startupDocumentPath);
    }
    if (diagnosticLogging) {
        AppendWideLiteral(buffer, L" --diagnostic-log");
    }
    return buffer.ok;
}

uint64_t TryReadProcessStartTimeUtcTicks(HANDLE process) {
    FILETIME creation{};
    FILETIME exit{};
    FILETIME kernel{};
    FILETIME user{};
    if (process == nullptr || GetProcessTimes(process, &creation, &exit, &kernel, &user) == FALSE) {
        return 0ULL;
    }

    return FileTimeToUInt64(creation) + kDotNetDateTimeEpochOffsetTicks;
}

void TryEarlyLaunchCodeService(CodeServiceBootstrapState &state) {
    if (InterlockedCompareExchange(&state.coreLaunchAttempted, 1L, 0L) != 0L) {
        return;
    }

    if (!state.identityReady || state.pid == 0U || state.processStartTimeUtcTicks == 0ULL) {
        return;
    }

    wchar_t modulePath[kPathCapacity]{};
    DWORD moduleError = 0U;
    if (!ResolveCurrentModulePath(modulePath, kPathCapacity, moduleError)) {
        return;
    }

    wchar_t configPath[kPathCapacity]{};
    if (!ResolveBootstrapConfigPath(modulePath, configPath, kPathCapacity)) {
        return;
    }

    BootstrapConfig config{};
    if (!ReadBootstrapConfig(configPath, config)) {
        return;
    }

    if (config.diagnosticLogging) {
        ActivateDiagnostics(state);
    }

    if (!config.enabled) {
        CaptureAndAppendBootstrapRecord(
            state,
            "codeservice_early_launch_skipped",
            "bootstrap_disabled",
            0U,
            0U,
            0ULL
        );
        return;
    }

    if (!ExactAsciiStringEquals(config.verifiedServiceVersion, kExpectedServiceVersion)) {
        CaptureAndAppendBootstrapRecord(
            state,
            "codeservice_early_launch_skipped",
            "bootstrap_version_mismatch",
            0U,
            0U,
            0ULL
        );
        return;
    }

    wchar_t projectRoot[kPathCapacity]{};
    DWORD projectRootError = 0U;
    if (!ResolveProjectRoot(modulePath, projectRoot, kPathCapacity, projectRootError)) {
        CaptureAndAppendBootstrapRecord(
            state,
            "codeservice_early_launch_skipped",
            "project_root_not_found",
            projectRootError,
            0U,
            0ULL
        );
        return;
    }

    wchar_t startupDocumentPath[kDocumentPathMaximumCharacters + 1U]{};
    const bool startupDocumentDiscovered = DiscoverStartupDocumentPath(
        state,
        projectRoot,
        startupDocumentPath,
        kDocumentPathMaximumCharacters + 1U
    );

    const wchar_t *transportStartupDocumentPath = nullptr;
    if (startupDocumentDiscovered) {
        if (startupDocumentPath[0] == L'-' && startupDocumentPath[1] == L'-') {
            CaptureAndAppendStartupDocumentRecord(
                state,
                "startup_document_transport_skipped",
                "cli_value_ambiguous",
                0U,
                nullptr,
                0U
            );
        } else {
            transportStartupDocumentPath = startupDocumentPath;
        }
    }

    wchar_t shimPath[kPathCapacity]{};
    DWORD shimError = 0U;
    if (!ResolveCanonicalServiceShim(shimPath, kPathCapacity, shimError)) {
        const char *reason = shimError == ERROR_ENVVAR_NOT_FOUND
            ? "user_profile_unavailable"
            : "service_shim_missing";
        CaptureAndAppendBootstrapRecord(
            state,
            "codeservice_early_launch_skipped",
            reason,
            shimError,
            0U,
            0ULL
        );
        return;
    }

    wchar_t commandLine[kCommandLineCapacity]{};
    if (!BuildServiceCommandLine(
            shimPath,
            state.pid,
            state.processStartTimeUtcTicks,
            projectRoot,
            transportStartupDocumentPath,
            config.diagnosticLogging,
            commandLine,
            kCommandLineCapacity)) {
        CaptureAndAppendBootstrapRecord(
            state,
            "codeservice_early_launch_skipped",
            "command_line_overflow",
            ERROR_INSUFFICIENT_BUFFER,
            0U,
            0ULL
        );
        return;
    }

    if (transportStartupDocumentPath != nullptr) {
        CaptureAndAppendStartupDocumentRecord(
            state,
            "startup_document_transport_attached",
            nullptr,
            0U,
            nullptr,
            0U
        );
    }

    STARTUPINFOW startupInfo{};
    startupInfo.cb = sizeof(startupInfo);
    PROCESS_INFORMATION processInfo{};

    LARGE_INTEGER attemptedCounter{};
    FILETIME attemptedFileTime{};
    const bool attemptedCounterReady = QueryPerformanceCounter(&attemptedCounter) != FALSE;
    GetSystemTimePreciseAsFileTime(&attemptedFileTime);

    BOOL created = CreateProcessW(
        shimPath,
        commandLine,
        nullptr,
        nullptr,
        FALSE,
        CREATE_NO_WINDOW,
        nullptr,
        projectRoot,
        &startupInfo,
        &processInfo
    );
    DWORD createError = created == FALSE ? GetLastError() : 0U;

    if (attemptedCounterReady) {
        AppendDiagnosticRecord(
            state,
            "codeservice_early_launch_attempted",
            "Core",
            attemptedFileTime,
            attemptedCounter,
            nullptr,
            0U,
            0U,
            0ULL,
            nullptr,
            0U
        );
    }

    if (created == FALSE) {
        CaptureAndAppendBootstrapRecord(
            state,
            "codeservice_early_launch_failed",
            "create_process_failed",
            createError,
            0U,
            0ULL
        );
        return;
    }

    const DWORD servicePid = processInfo.dwProcessId;
    const uint64_t serviceStartTimeUtcTicks = TryReadProcessStartTimeUtcTicks(processInfo.hProcess);

    if (processInfo.hThread != nullptr) {
        CloseHandle(processInfo.hThread);
    }
    if (processInfo.hProcess != nullptr) {
        CloseHandle(processInfo.hProcess);
    }

    CaptureAndAppendBootstrapRecord(
        state,
        "codeservice_early_launch_succeeded",
        nullptr,
        0U,
        servicePid,
        serviceStartTimeUtcTicks
    );
}

void InitializeCodeServiceBootstrap(
    void *p_userdata,
    GDExtensionInitializationLevel p_level
) {
    CodeServiceBootstrapState *state = static_cast<CodeServiceBootstrapState *>(p_userdata);
    if (state == nullptr) {
        return;
    }

    switch (p_level) {
        case GDEXTENSION_INITIALIZATION_CORE:
            CaptureCoreInitializationEvent(*state);
            TryEarlyLaunchCodeService(*state);
            break;
        case GDEXTENSION_INITIALIZATION_SERVERS:
            CaptureAndAppendInitializationEvent(
                *state,
                "gdextension_initialize_servers",
                "Servers"
            );
            break;
        case GDEXTENSION_INITIALIZATION_SCENE:
            CaptureAndAppendInitializationEvent(
                *state,
                "gdextension_initialize_scene",
                "Scene"
            );
            break;
        case GDEXTENSION_INITIALIZATION_EDITOR:
            CaptureAndAppendInitializationEvent(
                *state,
                "gdextension_initialize_editor",
                "Editor"
            );
            break;
        default:
            break;
    }
}

void DeinitializeCodeServiceBootstrap(
    void *p_userdata,
    GDExtensionInitializationLevel p_level
) {
    (void)p_userdata;
    (void)p_level;
}

} // namespace

extern "C" __declspec(dllexport) GDExtensionBool
code_service_bootstrap_init(
    GDExtensionInterfaceGetProcAddress p_get_proc_address,
    GDExtensionClassLibraryPtr p_library,
    GDExtensionInitialization *r_initialization
) {
    (void)p_get_proc_address;
    (void)p_library;

    LARGE_INTEGER entryCounter{};
    FILETIME entryFileTime{};

    const bool entryCounterReady = QueryPerformanceCounter(&entryCounter) != FALSE;
    GetSystemTimePreciseAsFileTime(&entryFileTime);

    InitializeStateFromEntry(g_state, entryFileTime, entryCounter, entryCounterReady);

    if (r_initialization == nullptr) {
        return static_cast<GDExtensionBool>(0);
    }

    r_initialization->minimum_initialization_level = GDEXTENSION_INITIALIZATION_CORE;
    r_initialization->userdata = &g_state;
    r_initialization->initialize = &InitializeCodeServiceBootstrap;
    r_initialization->deinitialize = &DeinitializeCodeServiceBootstrap;

    return static_cast<GDExtensionBool>(1);
}
