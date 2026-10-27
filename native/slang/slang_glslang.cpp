// libslang.so for the phone: the part of the Slang compiler's API that the engine uses, on top of glslang.
//
// The engine compiles a shader through Slang when it is not in a SPIR-V cache (the game's own shaders all are: the
// asset pack and the .spv-cache files beside the sources). What is left for the phone to compile are shaders mods bring
// as source (1.7's ShaderDef with vertexShaderFileName / fragmentShaderFileName, GLSL files in <mod>/<version>/Shaders):
// plain Vulkan GLSL, which glslang (the reference compiler, sources in the NDK under sources/third_party/shaderc)
// compiles just as well; all 220 of the game's own GLSL shaders pass through it without an error. There is no Slang
// build for Android.
//
// What the engine calls (Waylith.Rendering.RuntimeShaderCompiler through Brutal.Slang.dll):
//   slang_createGlobalSession2            -> a COM object; of IGlobalSession only findProfile and createCompileRequest
//   the request through the flat C API    -> spSetCodeGenTarget ... spAddTranslationUnitSourceString, spAddEntryPoint,
//                                            spCompile, spGetDiagnosticOutput, spGetEntryPointCode,
//                                            spGetDependencyFileCount/Path, spDestroyCompileRequest
// No reflection is asked of Slang: the engine reads the SPIR-V itself, and by name only loosely (it accepts Slang's
// "entryPointParam_main.outColor" as well as a plain "outColor").
//
// Differences from Slang that matter here:
//   - the define sampler=SamplerState, which the engine adds for Slang's GLSL front end when a source declares separate
//     samplers, is dropped: glslang knows the type sampler;
//   - #include works as in Slang (glslang wants GL_GOOGLE_include_directive, which is switched on in the preamble);
//   - matrices in uniform, push-constant and storage blocks are laid out by rows, as Slang lays them out for the engine
//     (it compiles with its "column major" matrix mode, which comes out as RowMajor in SPIR-V; GLSL's own default is
//     ColMajor). The engine fills the buffers for that layout: with glslang's default every transform arrived
//     transposed and the map drew as one red triangle. Everything else of the interface (locations, sets, bindings,
//     offsets, member names) is the same from both compilers;
//   - no optimizer runs (the NDK's glslang is built here without spirv-tools); the driver compiles the SPIR-V anyway.
#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>
#include <android/log.h>

#include "glslang/Public/ShaderLang.h"
#include "SPIRV/GlslangToSpv.h"
#include "libshaderc_util/resources.h"

#define API extern "C" __attribute__((visibility("default")))
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "port17", __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, "port17", __VA_ARGS__)

namespace {

const int kOk = 0;
const int kFail = (int)0x80004005;
const int kNoInterface = (int)0x80004002;
const int kNotImplemented = (int)0x80004001;

// SlangSourceLanguage and SlangStage, as far as they are used.
const int kLanguageHlsl = 2, kLanguageGlsl = 3;
const int kStageVertex = 1, kStageHull = 2, kStageDomain = 3, kStageGeometry = 4, kStageFragment = 5, kStageCompute = 6;

struct TranslationUnit
{
    int language;
    std::string name, path, source;
};

struct EntryPoint
{
    int unit;
    std::string name;
    int stage;
    std::vector<unsigned int> spirv;
};

struct Request
{
    std::vector<std::pair<std::string, std::string>> defines;
    std::vector<std::string> searchPaths;
    std::vector<TranslationUnit> units;
    std::vector<EntryPoint> entries;
    std::vector<std::string> dependencies;
    std::string diagnostics;
};

std::string DirectoryOf(const std::string& path)
{
    size_t slash = path.find_last_of("/\\");
    return slash == std::string::npos ? std::string() : path.substr(0, slash);
}

bool ReadFile(const std::string& path, std::string& out)
{
    FILE* file = fopen(path.c_str(), "rb");
    if (!file) return false;
    char buffer[16384];
    size_t n;
    out.clear();
    while ((n = fread(buffer, 1, sizeof buffer, file)) > 0) out.append(buffer, n);
    fclose(file);
    return true;
}

// #include: beside the including file first, then the request's search paths (the engine gives the folders of the
// game's and the mods' Shaders). The name handed back is the full path, so that an include inside it resolves beside it,
// and the path is remembered as a dependency (the engine stores the list with the compiled shader in its cache).
class Includer : public glslang::TShader::Includer
{
public:
    explicit Includer(Request& request) : request(request) {}

    IncludeResult* includeLocal(const char* header, const char* includer, size_t) override
    {
        std::string beside = DirectoryOf(includer ? includer : "");
        if (!beside.empty())
        {
            IncludeResult* found = Open(beside + "/" + header);
            if (found) return found;
        }
        return includeSystem(header, includer, 0);
    }

    IncludeResult* includeSystem(const char* header, const char*, size_t) override
    {
        for (const std::string& dir : request.searchPaths)
        {
            IncludeResult* found = Open(dir + "/" + header);
            if (found) return found;
        }
        return nullptr;
    }

    void releaseInclude(IncludeResult* result) override
    {
        if (result)
        {
            delete static_cast<std::string*>(result->userData);
            delete result;
        }
    }

private:
    IncludeResult* Open(const std::string& path)
    {
        std::string* content = new std::string();
        if (!ReadFile(path, *content))
        {
            delete content;
            return nullptr;
        }
        bool known = false;
        for (const std::string& d : request.dependencies) known = known || d == path;
        if (!known) request.dependencies.push_back(path);
        return new IncludeResult(path, content->data(), content->size(), content);
    }

    Request& request;
};

// The words a glslang info log calls reserved ("ERROR: file:113: 'common' : Reserved word."), each once.
std::vector<std::string> ReservedWordsIn(const std::string& log)
{
    std::vector<std::string> words;
    const std::string mark = "' : Reserved word";
    for (size_t at = log.find(mark); at != std::string::npos; at = log.find(mark, at + 1))
    {
        size_t open = log.rfind('\'', at - 1);
        if (open == std::string::npos) continue;
        std::string word = log.substr(open + 1, at - open - 1);
        bool plain = !word.empty();
        for (char c : word) plain = plain && (isalnum((unsigned char)c) || c == '_');
        if (plain && std::find(words.begin(), words.end(), word) == words.end()) words.push_back(word);
    }
    return words;
}

// Every whole-word occurrence of word in text as replacement.
std::string RenameWord(const std::string& text, const std::string& word, const std::string& replacement)
{
    std::string out;
    out.reserve(text.size() + 64);
    auto part = [](char c) { return isalnum((unsigned char)c) || c == '_'; };
    size_t i = 0;
    while (i < text.size())
    {
        if (text.compare(i, word.size(), word) == 0 && (i == 0 || !part(text[i - 1])) && (i + word.size() >= text.size() || !part(text[i + word.size()])))
        {
            out += replacement;
            i += word.size();
        }
        else
        {
            out += text[i++];
        }
    }
    return out;
}

bool StageOf(int stage, EShLanguage& out)
{
    switch (stage)
    {
    case kStageVertex: out = EShLangVertex; return true;
    case kStageHull: out = EShLangTessControl; return true;
    case kStageDomain: out = EShLangTessEvaluation; return true;
    case kStageGeometry: out = EShLangGeometry; return true;
    case kStageFragment: out = EShLangFragment; return true;
    case kStageCompute: out = EShLangCompute; return true;
    default: return false;
    }
}

// The GLSL source with "layout(row_major) uniform;" (and "buffer", from version 430 on) as the default for every block
// declared after it: inserted on a line of its own after the #version line, with a #line directive so that messages
// still name the source's own lines.
std::string WithMatricesByRows(const std::string& source)
{
    size_t at = 0, line = 1;
    int version = 0;
    bool found = false;
    while (at < source.size())
    {
        size_t end = source.find('\n', at);
        if (end == std::string::npos) end = source.size();
        size_t first = source.find_first_not_of(" \t", at);
        if (first != std::string::npos && first < end && source.compare(first, 8, "#version") == 0)
        {
            version = atoi(source.c_str() + first + 8);
            found = true;
            at = end < source.size() ? end + 1 : end;
            line++;
            break;
        }
        at = end < source.size() ? end + 1 : end;
        line++;
    }
    std::string defaults = "layout(row_major) uniform;";
    if (!found || version >= 430) defaults += " layout(row_major) buffer;";
    if (!found) return defaults + "\n#line 1\n" + source;
    std::string head = source.substr(0, at);
    if (head.empty() || head.back() != '\n') head += "\n";
    return head + defaults + "\n#line " + std::to_string(line) + "\n" + source.substr(at);
}

std::once_flag g_glslang;

bool CompileEntry(Request& request, EntryPoint& entry)
{
    if (entry.unit < 0 || entry.unit >= (int)request.units.size())
    {
        request.diagnostics += "entry point '" + entry.name + "' names no translation unit\n";
        return false;
    }
    const TranslationUnit& unit = request.units[entry.unit];
    EShLanguage stage;
    if (!StageOf(entry.stage, stage))
    {
        request.diagnostics += unit.path + ": stage " + std::to_string(entry.stage) + " is not supported\n";
        return false;
    }
    bool hlsl = unit.language == kLanguageHlsl;
    if (!hlsl && unit.language != kLanguageGlsl)
    {
        request.diagnostics += unit.path + ": only GLSL and HLSL sources can be compiled on the phone (the Slang language itself cannot)\n";
        return false;
    }
    std::call_once(g_glslang, [] { glslang::InitializeProcess(); });

    std::string preamble = "#extension GL_GOOGLE_include_directive : enable\n";
    for (const auto& define : request.defines)
    {
        if (define.first == "sampler") continue; // Slang's GLSL front end needs it; glslang knows "sampler"
        preamble += "#define " + define.first + " " + define.second + "\n";
    }
    const std::string& name = unit.path.empty() ? unit.name : unit.path;
    std::string text = hlsl ? unit.source : WithMatricesByRows(unit.source);
    EShMessages messages = (EShMessages)(EShMsgSpvRules | EShMsgVulkanRules | (hlsl ? EShMsgReadHlsl : 0));
    Includer includer(request);
    std::unique_ptr<glslang::TShader> shader;
    // A GLSL source that names a variable with a word glslang reserves (a mod's "float common = ..."; the PC's Slang takes
    // it) is parsed once more with those words renamed, the ones the first parse named only.
    for (int attempt = 0;; attempt++)
    {
        const char* strings[1] = { text.c_str() };
        const int lengths[1] = { (int)text.size() };
        const char* names[1] = { name.c_str() };
        shader.reset(new glslang::TShader(stage));
        shader->setStringsWithLengthsAndNames(strings, lengths, names, 1);
        shader->setPreamble(preamble.c_str());
        shader->setEntryPoint(entry.name.empty() ? "main" : entry.name.c_str());
        shader->setEnvInput(hlsl ? glslang::EShSourceHlsl : glslang::EShSourceGlsl, stage, glslang::EShClientVulkan, 110);
        shader->setEnvClient(glslang::EShClientVulkan, glslang::EShTargetVulkan_1_1);
        shader->setEnvTarget(glslang::EShTargetSpv, glslang::EShTargetSpv_1_3);
        shader->setAutoMapLocations(true);
        if (shader->parse(&shaderc_util::kDefaultTBuiltInResource, 110, false, messages, includer))
        {
            break;
        }
        std::string log = shader->getInfoLog();
        std::vector<std::string> words = ReservedWordsIn(log);
        if (hlsl || attempt > 0 || words.empty())
        {
            request.diagnostics += log;
            request.diagnostics += shader->getInfoDebugLog();
            return false;
        }
        for (const std::string& word : words)
        {
            text = RenameWord(text, word, word + "_port17");
        }
    }
    glslang::TProgram program;
    program.addShader(shader.get());
    if (!program.link(messages))
    {
        request.diagnostics += program.getInfoLog();
        request.diagnostics += program.getInfoDebugLog();
        return false;
    }
    glslang::TIntermediate* intermediate = program.getIntermediate(stage);
    if (!intermediate)
    {
        request.diagnostics += name + ": no code for the stage\n";
        return false;
    }
    spv::SpvBuildLogger logger;
    glslang::SpvOptions options;
    options.disableOptimizer = true;
    glslang::GlslangToSpv(*intermediate, entry.spirv, &logger, &options);
    std::string said = logger.getAllMessages();
    if (!said.empty()) request.diagnostics += said;
    if (entry.spirv.empty())
    {
        request.diagnostics += name + ": no SPIR-V came out\n";
        return false;
    }
    return true;
}

// ---- the global session: a COM object of which two methods are real ----

struct Guid
{
    uint32_t a;
    uint16_t b, c;
    uint8_t d[8];
};

const Guid kUnknown = { 0x00000000, 0x0000, 0x0000, { 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 } };
// slang::IGlobalSession: c140b5fd-0c78-452e-ba7c-1a1e70c7f71c
const Guid kGlobalSession = { 0xc140b5fd, 0x0c78, 0x452e, { 0xba, 0x7c, 0x1a, 0x1e, 0x70, 0xc7, 0xf7, 0x1c } };

struct Session
{
    void** vtable;
    std::atomic<int> references;
};

int SessionQueryInterface(Session* self, const Guid* iid, void** out)
{
    if (!out) return kFail;
    if (iid && (memcmp(iid, &kUnknown, sizeof(Guid)) == 0 || memcmp(iid, &kGlobalSession, sizeof(Guid)) == 0))
    {
        self->references++;
        *out = self;
        return kOk;
    }
    *out = nullptr;
    return kNoInterface;
}

unsigned int SessionAddRef(Session* self) { return (unsigned int)++self->references; }

unsigned int SessionRelease(Session* self)
{
    int left = --self->references;
    if (left == 0) delete self;
    return (unsigned int)left;
}

int SessionNotImplemented(Session*) { return kNotImplemented; }
int SessionOk(Session*) { return kOk; }

// findProfile: the engine asks for "spirv_1_3" and hands the id back to spSetTargetProfile; any id but 0 (unknown) does.
int SessionFindProfile(Session*, const char* name) { return name && *name ? 1 : 0; }

const char* SessionBuildTag(Session*);

int SessionCreateCompileRequest(Session*, Request** out)
{
    if (!out) return kFail;
    *out = new Request();
    return kOk;
}

// Slots as in slang.h (and Brutal.Slang's IGlobalSession): 0-2 IUnknown, 3 createSession, 4 findProfile,
// 8 getBuildTagString, 13 createCompileRequest, 17 checkCompileTargetSupport, 18 checkPassThroughSupport; 32 in all.
void* g_sessionVtable[40];
std::once_flag g_vtable;

void BuildVtable()
{
    for (void*& slot : g_sessionVtable) slot = (void*)SessionNotImplemented;
    g_sessionVtable[0] = (void*)SessionQueryInterface;
    g_sessionVtable[1] = (void*)SessionAddRef;
    g_sessionVtable[2] = (void*)SessionRelease;
    g_sessionVtable[4] = (void*)SessionFindProfile;
    g_sessionVtable[8] = (void*)SessionBuildTag;
    g_sessionVtable[13] = (void*)SessionCreateCompileRequest;
    g_sessionVtable[17] = (void*)SessionOk;
    g_sessionVtable[18] = (void*)SessionOk;
}

int CreateSession(void** out)
{
    if (!out) return kFail;
    std::call_once(g_vtable, BuildVtable);
    Session* session = new Session();
    session->vtable = g_sessionVtable;
    session->references = 1;
    *out = session;
    return kOk;
}

} // namespace

// The engine's cache keys include this tag; it is the one of the Slang build the game ships with, so that the shaders
// compiled with that (the asset pack, the .spv-cache files beside the sources) are found.
//
// For testing this compiler against the whole game: "shadertag=<anything>" among the options of <game>/vulkan.txt
// (VULKAN="shadertag=glslang1" host/run.sh ...; the host passes them in PORT17_VULKAN) reports another tag, so that
// no cache entry matches and every shader the game uses is compiled here.
API const char* spGetBuildTagString(void)
{
    static char tag[64];
    if (!tag[0])
    {
        const char* options = getenv("PORT17_VULKAN");
        const char* at = options ? strstr(options, "shadertag=") : nullptr;
        if (at)
        {
            at += 10;
            size_t n = 0;
            while (at[n] && at[n] != ' ' && n + 1 < sizeof tag) n++;
            memcpy(tag, at, n);
            tag[n] = 0;
            LOGI("shader compiler (glslang): reporting the build tag '%s' (test: nothing comes from the caches)", tag);
        }
        if (!tag[0]) strcpy(tag, "2026.19");
    }
    return tag;
}

namespace { const char* SessionBuildTag(Session*) { return spGetBuildTagString(); } }

API int slang_createGlobalSession(int, void** out) { return CreateSession(out); }
API int slang_createGlobalSession2(const void*, void** out) { return CreateSession(out); }
API int slang_createGlobalSessionWithoutCoreModule(int, void** out) { return CreateSession(out); }
API void slang_shutdown(void) {}

API void spDestroyCompileRequest(Request* request) { delete request; }
API void spSetCodeGenTarget(Request*, int) {}
API void spSetTargetProfile(Request*, int, int) {}
API void spSetMatrixLayoutMode(Request*, int) {}
API void spSetDebugInfoLevel(Request*, int) {}
API void spSetOptimizationLevel(Request*, int) {}
API int spProcessCommandLineArguments(Request*, const char* const*, int) { return kOk; } // "-emit-spirv-via-glsl": it is glslang anyway

API void spAddSearchPath(Request* request, const char* path)
{
    if (request && path) request->searchPaths.push_back(path);
}

API void spAddPreprocessorDefine(Request* request, const char* key, const char* value)
{
    if (request && key) request->defines.emplace_back(key, value ? value : "");
}

API int spAddTranslationUnit(Request* request, int language, const char* name)
{
    if (!request) return -1;
    TranslationUnit unit;
    unit.language = language;
    unit.name = name ? name : "";
    request->units.push_back(unit);
    return (int)request->units.size() - 1;
}

API void spAddTranslationUnitSourceString(Request* request, int unit, const char* path, const char* source)
{
    if (!request || unit < 0 || unit >= (int)request->units.size()) return;
    request->units[unit].path = path ? path : "";
    request->units[unit].source = source ? source : "";
}

API int spAddEntryPoint(Request* request, int unit, const char* name, int stage)
{
    if (!request) return -1;
    EntryPoint entry;
    entry.unit = unit;
    entry.name = name ? name : "main";
    entry.stage = stage;
    request->entries.push_back(entry);
    return (int)request->entries.size() - 1;
}

API int spCompile(Request* request)
{
    if (!request) return kFail;
    bool ok = true;
    for (EntryPoint& entry : request->entries)
    {
        ok = CompileEntry(*request, entry) && ok;
    }
    const char* what = request->units.empty() ? "?" : request->units[0].path.c_str();
    if (ok) LOGI("shader compiler (glslang): %s: %d entry point(s) compiled", what, (int)request->entries.size());
    else LOGW("shader compiler (glslang): %s failed: %.900s", what, request->diagnostics.c_str());
    return ok ? kOk : kFail;
}

API const char* spGetDiagnosticOutput(Request* request) { return request ? request->diagnostics.c_str() : ""; }

API int spGetDependencyFileCount(Request* request) { return request ? (int)request->dependencies.size() : 0; }

API const char* spGetDependencyFilePath(Request* request, int index)
{
    return request && index >= 0 && index < (int)request->dependencies.size() ? request->dependencies[index].c_str() : nullptr;
}

API const void* spGetEntryPointCode(Request* request, int index, size_t* outSize)
{
    if (outSize) *outSize = 0;
    if (!request || index < 0 || index >= (int)request->entries.size() || request->entries[index].spirv.empty()) return nullptr;
    if (outSize) *outSize = request->entries[index].spirv.size() * sizeof(unsigned int);
    return request->entries[index].spirv.data();
}
