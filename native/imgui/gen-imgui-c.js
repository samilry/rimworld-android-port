// Generates the flat C API over Dear ImGui that the engine's Brutal.ImGui binds (imgui.dll on Windows exports
// it next to the C++ symbols). The export names follow the binding generator's scheme:
//   Name              a function in namespace ImGui (imgui.h), or a global function
//   Name_N            the N-th overload of it, in declaration order
//   Name_internal[_N] the same for imgui_internal.h
//   Type_Method[...]  a member function; the first argument is the object ("target")
// Reference parameters (const ImVec2&) become pointers, everything else is passed as declared. The C++ overload is
// picked by position, so the wrapper is generated from clang's AST of the headers, not from guesses.
//
// usage: node gen-imgui-c.js <imgui-ast.json> <Brutal.ImGui.decompiled.cs> <exports.txt> <out.cpp>
const fs = require('fs');
const [astPath, csPath, exportsPath, outPath] = process.argv.slice(2);

const ast = JSON.parse(fs.readFileSync(astPath, 'utf8'));
const cs = fs.readFileSync(csPath, 'utf8');
const exportNames = fs.readFileSync(exportsPath, 'utf8').split(/\r?\n/).map(s => s.trim()).filter(s => s && !s.startsWith('?'));

// ---- the headers' declarations, in order ----
const nsFuncs = {};    // name -> [decl]
const globalFuncs = {};
const records = {};    // name -> { internal, methods: { name -> [decl] } }
let curFile = '';

function noteFile(node) {
	const loc = node.loc || {};
	const f = loc.file || (loc.expansionLoc && loc.expansionLoc.file) || (loc.spellingLoc && loc.spellingLoc.file);
	if (f) curFile = f.replace(/\\/g, '/');
	else if (node.range && node.range.begin && node.range.begin.file) curFile = node.range.begin.file.replace(/\\/g, '/');
}

function isInternalFile(f) { return /imgui_internal\.h$/.test(f); }
function isImGuiFile(f) { return /imgui\.h$|imgui_internal\.h$/.test(f); }

function returnTypeOf(qualType) {
	let depth = 0;
	for (let i = 0; i < qualType.length; i++) {
		const c = qualType[i];
		if (c === '(' && depth === 0) return qualType.slice(0, i).trim();
		if (c === '<') depth++;
		if (c === '>') depth--;
	}
	throw new Error('no parameter list in ' + qualType);
}

function normalizeType(t) {
	t = t.replace(/\s+/g, ' ').trim();
	const arr = t.match(/^(.*?)\s*\[(\d*)\]$/);
	if (arr) t = arr[1].trim() + ' *';
	return t;
}

function readFunction(node) {
	const params = [];
	for (const c of node.inner || []) {
		if (c.kind === 'ParmVarDecl') params.push({ name: c.name || '', type: normalizeType(c.type.qualType) });
	}
	return {
		name: node.name,
		file: curFile,
		internal: isInternalFile(curFile),
		ret: normalizeType(returnTypeOf(node.type.qualType)),
		params,
		variadic: !!node.variadic,
		isStatic: node.storageClass === 'static',
		isConst: / const$/.test(node.type.qualType),
	};
}

function walk(node, ctx) {
	if (!node || typeof node !== 'object') return;
	noteFile(node);
	if (node.isImplicit) return;
	switch (node.kind) {
	case 'NamespaceDecl':
		for (const c of node.inner || []) walk(c, { ns: node.name });
		return;
	case 'FunctionDecl':
		if (!isImGuiFile(curFile)) return;
		{
			const table = ctx.ns === 'ImGui' ? nsFuncs : (ctx.ns ? null : globalFuncs);
			if (!table) return;
			const d = readFunction(node);
			if (!node.previousDecl) (table[d.name] = table[d.name] || []).push(d);
			else {
				// A redeclaration (definition after a declaration): keep the first.
				const list = table[d.name];
				if (!list || !list.some(x => sameSig(x, d))) (table[d.name] = list || []).push(d);
			}
		}
		return;
	case 'ClassTemplateDecl':
	case 'ClassTemplateSpecializationDecl':
	case 'ClassTemplatePartialSpecializationDecl':
		return;
	case 'CXXRecordDecl':
		if (!isImGuiFile(curFile) || !node.completeDefinition || !node.name) return;
		if (ctx.inRecord) return; // nested records: none we need
		{
			const rec = { name: node.name, internal: isInternalFile(curFile), file: curFile, methods: {} };
			if (records[node.name]) return;
			records[node.name] = rec;
			for (const c of node.inner || []) {
				noteFile(c);
				if (c.kind !== 'CXXMethodDecl' || c.isImplicit || !c.name || c.name.startsWith('operator')) continue;
				const d = readFunction(c);
				(rec.methods[d.name] = rec.methods[d.name] || []).push(d);
			}
		}
		return;
	default:
		for (const c of node.inner || []) walk(c, ctx);
	}
}

function sameSig(a, b) {
	return a.ret === b.ret && a.params.length === b.params.length && a.params.every((p, i) => p.type === b.params[i].type);
}

walk(ast, {});
console.error(`headers: ${Object.keys(nsFuncs).length} ImGui functions, ${Object.keys(globalFuncs).length} global functions, ${Object.keys(records).length} records`);

// ---- the managed declarations, for a parameter-count check ----
const csParams = {};
{
	const re = /(?:\[LibraryImport\("imgui"[^\]]*\]|\[DllImport\("imgui", EntryPoint = "([A-Za-z0-9_]+)"[^\]]*\])\s*(?:\[[^\]]*\]\s*)*(?:public |private |internal )?(?:unsafe )?static (?:extern |partial )?(?:unsafe )?([^\s(]+)\s+([A-Za-z0-9_]+)\(([^)]*)\)/g;
	let m;
	while ((m = re.exec(cs)) !== null) {
		const name = m[1] || m[3];
		const params = m[4].trim() === '' ? [] : m[4].split(',').map(s => s.trim());
		if (!csParams[name]) csParams[name] = params;
	}
	console.error(`managed: ${Object.keys(csParams).length} imports`);
}

// ---- resolution ----
function resolve(name) {
	const m = name.match(/^(.*?)(_internal)?(?:_(\d+))?$/);
	const base = m[1];
	const internal = !!m[2];
	const index = m[3] === undefined ? undefined : parseInt(m[3], 10);
	const pick = (list, what) => {
		const filtered = list.filter(d => d.internal === internal);
		if (filtered.length === 0) return null;
		if (index === undefined) {
			if (filtered.length !== 1) { console.error(`  ${name}: ${what} has ${filtered.length} overloads, index missing; taking the first`); }
			return filtered[0];
		}
		if (index >= filtered.length) { console.error(`  ${name}: ${what} has only ${filtered.length} overloads`); return null; }
		return filtered[index];
	};
	if (nsFuncs[base]) {
		const d = pick(nsFuncs[base], 'ImGui::' + base);
		if (d) return { kind: 'ns', decl: d };
	}
	if (globalFuncs[base]) {
		const d = pick(globalFuncs[base], base);
		if (d) return { kind: 'global', decl: d };
	}
	const us = base.indexOf('_');
	if (us > 0) {
		const type = base.slice(0, us), method = base.slice(us + 1);
		const rec = records[type];
		// ImDrawList_OnChangedClipRect binds ImDrawList::_OnChangedClipRect: the export name drops the leading underscore.
		const found = rec && (rec.methods[method] || rec.methods["_" + method]);
		if (found) {
			const list = found.map(d => Object.assign({}, d, { internal: rec.internal }));
			const d = pick(list, type + '::' + method);
			if (d) return { kind: d.isStatic ? 'static' : 'method', record: type, decl: d };
		}
	}
	return null;
}

// ---- emission ----
const out = [];
out.push('// Generated by native/imgui/gen-imgui-c.js: the flat C API over Dear ImGui that Brutal.ImGui binds.');
out.push('#include "imgui.h"');
out.push('#include "imgui_internal.h"');
out.push('#define EXPORT __attribute__((visibility("default")))');
out.push('');

function isRef(t) { return /&$/.test(t) && !/&&$/.test(t); }
function derefType(t) { return t.replace(/\s*&$/, ''); }
function cIdent(s) { return s.replace(/[^A-Za-z0-9_]/g, '_'); }

let generated = 0;
const missing = [];
const special = new Set(['Platform_GetWindowPos_ManagedFunctionPointer', 'Platform_GetWindowSize_ManagedFunctionPointer', 'Platform_GetWindowFramebufferScale_ManagedFunctionPointer',
	'Get_GetWindowPos_InteropPointer', 'Get_GetWindowSize_InteropPointer', 'Get_GetWindowFramebufferScale_InteropPointer']);

for (const name of exportNames) {
	if (special.has(name)) continue;
	const r = resolve(name);
	if (!r) { missing.push(name); continue; }
	const d = r.decl;
	const ns = 'w_' + cIdent(name);
	const lines = [];
	lines.push(`namespace ${ns} {`);
	lines.push(`\tusing R = ${isRef(d.ret) ? derefType(d.ret) : d.ret};`);
	d.params.forEach((p, i) => lines.push(`\tusing A${i} = ${isRef(p.type) ? derefType(p.type) : p.type};`));
	lines.push('}');
	const sig = [];
	if (r.kind === 'method' || r.kind === 'static') sig.push(`${r.record}* target`);
	d.params.forEach((p, i) => sig.push(`${ns}::A${i}${isRef(p.type) ? '*' : ''} a${i}`));
	const retType = `${ns}::R${isRef(d.ret) ? '*' : ''}`;
	const args = d.params.map((p, i) => {
		let a = isRef(p.type) ? `*a${i}` : `a${i}`;
		if (d.variadic && i === d.params.length - 1 && /^(const )?char \*$/.test(p.type)) a = `"%s", ${a}`;
		return a;
	}).join(', ');
	let call;
	if (r.kind === 'ns') call = `ImGui::${d.name}(${args})`;
	else if (r.kind === 'global') call = `::${d.name}(${args})`;
	else if (r.kind === 'static') call = `${r.record}::${d.name}(${args})`;
	else call = `target->${d.name}(${args})`;
	const fn = `${ns}_fn`;
	lines.push(`EXPORT ${retType} ${fn}(${sig.join(', ')}) __asm__("${name}");`);
	if (d.ret === 'void') lines.push(`${retType} ${fn}(${sig.join(', ')}) { ${call}; }`);
	else if (isRef(d.ret)) lines.push(`${retType} ${fn}(${sig.join(', ')}) { return &(${call}); }`);
	else lines.push(`${retType} ${fn}(${sig.join(', ')}) { return ${call}; }`);
	out.push(lines.join('\n'));
	generated++;

	const csp = csParams[name];
	if (csp) {
		const expected = d.params.length + (r.kind === 'method' || r.kind === 'static' ? 1 : 0);
		if (csp.length !== expected) console.error(`  ${name}: managed side has ${csp.length} parameters, C++ ${expected} (${d.params.map(p => p.type).join(', ')})`);
	}
}

// Viewport platform callbacks returning ImVec2 go through C trampolines (as the Windows build does).
for (const fn of ['GetWindowPos', 'GetWindowSize', 'GetWindowFramebufferScale']) {
	out.push(`extern "C" { EXPORT ImVec2 (*Platform_${fn}_ManagedFunctionPointer)(ImGuiViewport*) = nullptr; }`);
	out.push(`static ImVec2 Platform_${fn}_Trampoline(ImGuiViewport* viewport) { return Platform_${fn}_ManagedFunctionPointer(viewport); }`);
	out.push(`extern "C" EXPORT void* Get_${fn}_InteropPointer() { return (void*)&Platform_${fn}_Trampoline; }`);
}

// Struct sizes the managed side assumes (from the x64 Windows build; the layouts must match here too).
out.push('');
const sizeRe = /\[StructLayout\(LayoutKind\.Explicit, Size = (\d+)\)\]\s*public (?:unsafe )?struct (Im[A-Za-z0-9]+)\b/g;
let sm, asserts = 0;
while ((sm = sizeRe.exec(cs)) !== null) {
	const type = sm[2], size = parseInt(sm[1], 10);
	if (!records[type]) continue;
	out.push(`static_assert(sizeof(${type}) == ${size}, "${type}: the managed side assumes ${size} bytes");`);
	asserts++;
}

fs.writeFileSync(outPath, out.join('\n') + '\n');
console.error(`generated ${generated} wrappers, ${asserts} size checks; ${missing.length} names not resolved`);
if (missing.length) console.error('  missing: ' + missing.join(' '));
