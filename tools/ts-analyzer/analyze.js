#!/usr/bin/env node
/* eslint-disable */
"use strict";

/**
 * CodeRag TypeScript analyzer sidecar.
 *
 * Spawned by the .NET TsCompilerAnalyzer. Loads a TypeScript project via the
 * TS Compiler API (through ts-morph) so that symbol references are resolved
 * with the real type checker — call edges, inheritance edges, and member
 * references all carry a stable nodeId that the .NET side translates into
 * a GUID, eliminating the bare-name heuristics required by the tree-sitter
 * analyzer.
 *
 * Two invocation modes:
 *
 *   Batch:   node analyze.js --project <tsconfig.json>
 *            Loads, emits NDJSON, exits.
 *
 *   Server:  node analyze.js --server
 *            Reads NDJSON requests on stdin, writes NDJSON responses on stdout.
 *            Used by the .NET wrapper for incremental file-change reanalysis.
 *
 * Output is a stream of newline-delimited JSON records. Each record has a
 * "type" field — "chunk", "edge", "log", "error", or "done". Consumers should
 * read line-by-line; never attempt to parse stdout as a single JSON document.
 */

const path = require("path");
const fs = require("fs");

let tsMorph;
try {
  tsMorph = require("ts-morph");
} catch (e) {
  process.stderr.write(
    "ts-morph not installed. Run `npm install` in tools/ts-analyzer first.\n"
  );
  process.exit(2);
}

const { Project, SyntaxKind, Node, ts } = tsMorph;

// ─── stdout helpers ──────────────────────────────────────────────────────────

// Optional emit filter: when set to a Set<string> of forward-slash rel paths,
// only records whose `filePath` field is in the set are written. Records
// without a `filePath` (log/error/done/opened) always pass through. Used by
// the server-mode `reanalyze` op so we register chunks for the whole project
// (preserving cross-file edge target resolution) but emit only the
// chunks/edges that belong to the files the caller asked us to refresh.
let _emitFilter = null;
function setEmitFilter(filter) { _emitFilter = filter; }

function emit(record) {
  if (_emitFilter && record.filePath && !_emitFilter.has(record.filePath)) return;
  process.stdout.write(JSON.stringify(record) + "\n");
}
function emitLog(message) {
  emit({ type: "log", message });
}
function emitError(message, detail) {
  emit({ type: "error", message, detail: detail ? String(detail) : undefined });
}

// ─── project loading ────────────────────────────────────────────────────────

/**
 * Load a project from a tsconfig.json, or fall back to a directory scan.
 * Returns { project, rootDir } where rootDir is the absolute project root used
 * to compute relative file paths in the output.
 */
function loadProject(input) {
  const abs = path.resolve(input);
  const stat = fs.statSync(abs);

  if (stat.isFile() && abs.endsWith(".json")) {
    const project = new Project({
      tsConfigFilePath: abs,
      skipAddingFilesFromTsConfig: false,
      skipFileDependencyResolution: false,
    });
    const rootDir = path.dirname(abs);

    // Solution-style / composite tsconfigs use `"files": []` with
    // `"references": [...]` to point at sub-projects. ts-morph only loads the
    // root config and so gets 0 source files — it doesn't recurse into
    // referenced projects automatically. Detect this and pull in files from
    // each referenced tsconfig so the full project is analysable.
    const nonDecl = (sf) => !sf.isDeclarationFile() && !sf.isInNodeModules();
    if (project.getSourceFiles().filter(nonDecl).length === 0) {
      try {
        const tsconfigJson = JSON.parse(fs.readFileSync(abs, "utf8"));
        for (const ref of tsconfigJson.references || []) {
          let refConfig = path.resolve(rootDir, ref.path);
          // ref.path may point to a directory — check for tsconfig.json inside.
          if (!refConfig.endsWith(".json")) {
            refConfig = path.join(refConfig, "tsconfig.json");
          }
          if (fs.existsSync(refConfig)) {
            project.addSourceFilesFromTsConfig(refConfig);
          }
        }
      } catch {
        // Best-effort — fall through with whatever files loaded from the root.
      }
    }

    return { project, rootDir };
  }

  // Directory mode — scan for tsconfig.json, else add all .ts/.tsx files.
  const tsconfigPath = path.join(abs, "tsconfig.json");
  if (fs.existsSync(tsconfigPath)) {
    return {
      project: new Project({ tsConfigFilePath: tsconfigPath }),
      rootDir: abs,
    };
  }

  const project = new Project({
    compilerOptions: {
      allowJs: false,
      jsx: ts.JsxEmit.Preserve,
      target: ts.ScriptTarget.ES2022,
      moduleResolution: ts.ModuleResolutionKind.NodeJs,
      strict: false,
      noEmit: true,
    },
  });
  project.addSourceFilesAtPaths([
    path.join(abs, "**/*.ts"),
    path.join(abs, "**/*.tsx"),
    `!${path.join(abs, "**/node_modules/**")}`,
    `!${path.join(abs, "**/*.d.ts")}`,
  ]);
  return { project, rootDir: abs };
}

// ─── extraction ─────────────────────────────────────────────────────────────

/**
 * Stable per-project identifier for a chunk. Stays the same across reanalyses
 * as long as the declaration's file + kind + name + start line don't move.
 */
function makeNodeId(relPath, kind, name, startLine) {
  return `${relPath}#${kind}#${name}#${startLine}`;
}

function relPath(rootDir, sourceFile) {
  return path
    .relative(rootDir, sourceFile.getFilePath())
    .replace(/\\/g, "/");
}

function deriveNamespace(rel) {
  const dir = path.posix.dirname(rel);
  if (!dir || dir === ".") return null;
  return dir.replace(/[\\/]/g, ".");
}

function deriveFileClassName(rel) {
  const base = path.posix.basename(rel);
  const dot = base.indexOf(".");
  return dot < 0 ? base : base.slice(0, dot);
}

function jsDocText(node) {
  if (!Node.isJSDocable(node)) return null;
  const docs = node.getJsDocs();
  if (docs.length === 0) return null;
  return docs.map((d) => d.getInnerText()).join("\n").trim() || null;
}

function modifiersOf(node) {
  if (!Node.isModifierable(node)) return [];
  return node
    .getModifiers()
    .map((m) => m.getText())
    .filter((t) => t.length > 0);
}

function formatParams(node) {
  if (!node.getParameters) return [];
  return node.getParameters().map((p) => {
    const name = p.getName();
    const typeNode = p.getTypeNode();
    const t = typeNode ? typeNode.getText() : null;
    const opt = p.hasQuestionToken() ? "?" : "";
    return t ? `${name}${opt}: ${t}` : `${name}${opt}`;
  });
}

function returnTypeOf(node) {
  if (node.getReturnTypeNode) {
    const t = node.getReturnTypeNode();
    if (t) return t.getText();
  }
  if (node.getReturnType) {
    try {
      return node.getReturnType().getText(node);
    } catch {
      return null;
    }
  }
  return null;
}

function buildSignature(ns, className, name, params, ret) {
  let prefix = "";
  if (ns && className) prefix = `${ns}.${className}.`;
  else if (className) prefix = `${className}.`;
  else if (ns) prefix = `${ns}.`;
  const r = ret ? `: ${ret}` : "";
  return `${prefix}${name}(${params.join(", ")})${r}`;
}

/**
 * Compute the chunk a TS AST node belongs to by walking up to its nearest
 * extracted-chunk ancestor (class, interface, function, method, arrow-bound
 * variable). Returns the ancestor's nodeId, or null if the node is at file
 * top level outside any callable (in which case we attribute to the synthetic
 * file-level "<module init>" chunk if needed — currently we just skip).
 */
function findOwnerNodeId(node, chunkIndex) {
  for (let cur = node.getParent(); cur; cur = cur.getParent()) {
    const id = chunkIndex.byNode.get(cur);
    if (id) return id;
  }
  return null;
}

/**
 * Resolve a call/new-expression's target to a declaration that lives in the
 * project. Returns { nodeId, signatureText, name, isExternal }.
 *   - nodeId is set when the target is a declaration we extracted as a chunk
 *   - isExternal is true when the symbol resolves to a .d.ts in node_modules
 *     or the lib (DOM, ES, React types, etc.)
 *   - on total failure we fall back to the call text and isExternal=true
 */
function resolveCallTarget(expression, chunkIndex, project) {
  let calleeName = expression.getText();
  let dot = calleeName.lastIndexOf(".");
  let shortName = dot >= 0 ? calleeName.slice(dot + 1) : calleeName;

  let symbol = null;
  try {
    symbol = expression.getSymbol();
  } catch {
    // some constructs throw inside the checker; treat as external
  }
  if (!symbol) {
    return {
      nodeId: null,
      signatureText: calleeName,
      name: shortName,
      isExternal: true,
    };
  }

  // Follow aliases (import { X } from "...") to the real declaration.
  let aliased = symbol;
  try {
    aliased = symbol.getAliasedSymbol() || symbol;
  } catch {}

  const decls = aliased.getDeclarations();
  if (!decls || decls.length === 0) {
    return {
      nodeId: null,
      signatureText: calleeName,
      name: shortName,
      isExternal: true,
    };
  }

  // Prefer a project (non-.d.ts, non-node_modules) declaration.
  let pick = decls.find((d) => {
    const f = d.getSourceFile();
    if (f.isDeclarationFile()) return false;
    if (f.isInNodeModules()) return false;
    return true;
  });
  if (!pick) {
    // External: target is a library / .d.ts.
    const extDoc = project ? getSymbolDoc(aliased, project) : null;
    const extMeta = getExternalSymbolMeta(aliased);
    const extPkgPath = decls[0] ? decls[0].getSourceFile().getFilePath() : null;
    return {
      nodeId: null,
      signatureText: calleeName,
      name: aliased.getName() || shortName,
      isExternal: true,
      documentation: extDoc || undefined,
      packageName: extractPackageFromPath(extPkgPath) || undefined,
      namespace: extMeta.namespace || undefined,
      className: extMeta.className || undefined,
    };
  }

  const nodeId = chunkIndex.byNode.get(pick) || null;
  return {
    nodeId,
    signatureText: nodeId
      ? chunkIndex.signatureById.get(nodeId) || calleeName
      : calleeName,
    name: aliased.getName() || shortName,
    isExternal: nodeId === null,
  };
}

// ─── external library metadata ───────────────────────────────────────────────

/**
 * Extract the npm package name from a file path that contains node_modules.
 * Handles scoped packages like @scope/pkg.
 */
function extractPackageFromPath(filePath) {
  if (!filePath) return null;
  const fwd = filePath.replace(/\\/g, "/");
  const m = fwd.match(/node_modules\/((@[^/]+\/[^/]+)|([^/]+))/);
  return m ? m[1] : null;
}

/**
 * Extract plain-text documentation from a ts-morph symbol using TypeScript's
 * own documentation-comment API (works for .d.ts / node_modules symbols).
 * Falls back to walking the declaration's JSDoc AST nodes directly.
 */
function getSymbolDoc(symbol, project) {
  if (!symbol) return null;
  try {
    const checker = project.getTypeChecker().compilerObject;
    const compSym = symbol.compilerSymbol;
    const parts = compSym.getDocumentationComment(checker);
    if (parts && parts.length > 0) {
      const text = ts.displayPartsToString(parts).trim();
      if (text) {
        const tags = compSym.getJsDocTags(checker);
        const tagLines = (tags || []).map((tag) => {
          const content = ts.displayPartsToString(tag.text || []);
          return `@${tag.name}${content ? " " + content : ""}`;
        });
        return tagLines.length ? `${text}\n${tagLines.join("\n")}` : text;
      }
    }
  } catch {}
  // Fallback: JSDoc nodes attached to the declaration AST node
  try {
    for (const decl of symbol.getDeclarations() || []) {
      if (Node.isJSDocable(decl)) {
        const docs = decl.getJsDocs();
        if (docs.length > 0) {
          const text = docs.map((d) => d.getInnerText()).join("\n").trim();
          if (text) return text;
        }
      }
    }
  } catch {}
  return null;
}

/**
 * Walk a ts-morph symbol's parent chain to extract the containing class /
 * interface name and module namespace. Used to populate TargetClassName and
 * TargetNamespace on external library call edges.
 */
function getExternalSymbolMeta(symbol) {
  if (!symbol) return {};
  try {
    const parent = symbol.compilerSymbol.parent;
    if (!parent) return {};
    const classFlag = ts.SymbolFlags.Class | ts.SymbolFlags.Interface | ts.SymbolFlags.TypeLiteral;
    if (parent.flags & classFlag) {
      const grandParent = parent.parent;
      const ns =
        grandParent && grandParent.name && grandParent.name !== "__global"
          ? grandParent.name
          : null;
      return { className: parent.name, namespace: ns };
    }
    // Parent is a module / namespace
    const ns = parent.name && parent.name !== "__global" ? parent.name : null;
    return { namespace: ns };
  } catch {}
  return {};
}

// ─── chunk emission ─────────────────────────────────────────────────────────

function visitSourceFile(sourceFile, rootDir, chunkIndex, edges) {
  const rel = relPath(rootDir, sourceFile);
  const ns = deriveNamespace(rel);
  const fileClass = deriveFileClassName(rel);

  // First pass: register every chunk so call resolution in the second pass
  // can map symbols → nodeIds.
  for (const cls of sourceFile.getClasses()) registerClassish(cls, "class_declaration", rel, ns, chunkIndex, edges);
  for (const iface of sourceFile.getInterfaces()) registerClassish(iface, "interface_declaration", rel, ns, chunkIndex, edges);
  for (const ta of sourceFile.getTypeAliases()) registerTypeAlias(ta, rel, ns, chunkIndex);
  for (const fn of sourceFile.getFunctions()) registerFunction(fn, rel, ns, fileClass, chunkIndex);
  for (const vs of sourceFile.getVariableStatements()) registerArrowVarStatement(vs, rel, ns, fileClass, chunkIndex);
}

function registerClassish(node, kind, rel, ns, chunkIndex, edges) {
  const name = node.getName();
  if (!name) return;
  const start = node.getStartLineNumber();
  const end = node.getEndLineNumber();
  const nodeId = makeNodeId(rel, kind, name, start);
  const signature = buildSignature(ns, null, name, [], null).replace(/\(\)$/, "");

  chunkIndex.byNode.set(node, nodeId);
  chunkIndex.signatureById.set(nodeId, signature);

  const baseTypes = [];
  const interfaces = [];
  if (kind === "class_declaration") {
    const ext = node.getExtends && node.getExtends();
    if (ext) baseTypes.push(ext.getText());
    for (const impl of node.getImplements ? node.getImplements() : [])
      interfaces.push(impl.getText());
  } else {
    for (const ext of node.getExtends ? node.getExtends() : [])
      baseTypes.push(ext.getText());
  }

  emit({
    type: "chunk",
    nodeId,
    kind,
    name,
    namespace: ns,
    className: name,
    functionName: name,
    signature,
    filePath: rel,
    startLine: start,
    endLine: end,
    body: node.getText(),
    documentation: jsDocText(node),
    returnType: null,
    parameters: [],
    baseTypes,
    interfaces,
    modifiers: modifiersOf(node),
  });

  // Inheritance edges
  for (const b of baseTypes) {
    edges.push({
      source: node,
      sourceId: nodeId,
      targetText: b,
      kind: "inherits",
      line: start,
    });
  }
  for (const i of interfaces) {
    edges.push({
      source: node,
      sourceId: nodeId,
      targetText: i,
      kind: "implements",
      line: start,
    });
  }

  // Methods and properties on classes/interfaces
  const getters = [
    node.getMethods ? node.getMethods() : [],
    node.getProperties ? node.getProperties() : [],
    node.getConstructors ? node.getConstructors() : [],
  ];
  for (const list of getters) {
    for (const m of list) registerClassMember(m, rel, ns, name, chunkIndex);
  }
}

function registerClassMember(member, rel, ns, className, chunkIndex) {
  const memberName = member.getName ? member.getName() : "constructor";
  if (!memberName) return;
  const start = member.getStartLineNumber();
  const end = member.getEndLineNumber();
  let kind = "method_declaration";
  if (member.getKind() === SyntaxKind.PropertyDeclaration ||
      member.getKind() === SyntaxKind.PropertySignature)
    kind = "property_declaration";
  else if (member.getKind() === SyntaxKind.Constructor)
    kind = "constructor_declaration";

  const params = formatParams(member);
  const ret = returnTypeOf(member);
  const signature = buildSignature(ns, className, memberName, params, ret);
  const nodeId = makeNodeId(rel, kind, `${className}.${memberName}`, start);

  chunkIndex.byNode.set(member, nodeId);
  chunkIndex.signatureById.set(nodeId, signature);

  emit({
    type: "chunk",
    nodeId,
    kind,
    name: memberName,
    namespace: ns,
    className,
    functionName: memberName,
    signature,
    filePath: rel,
    startLine: start,
    endLine: end,
    body: member.getText(),
    documentation: jsDocText(member),
    returnType: ret,
    parameters: params,
    baseTypes: [],
    interfaces: [],
    modifiers: modifiersOf(member),
  });
}

function registerTypeAlias(node, rel, ns, chunkIndex) {
  const name = node.getName();
  if (!name) return;
  const start = node.getStartLineNumber();
  const end = node.getEndLineNumber();
  const nodeId = makeNodeId(rel, "type_alias_declaration", name, start);
  const signature = buildSignature(ns, null, name, [], null).replace(/\(\)$/, "");
  chunkIndex.byNode.set(node, nodeId);
  chunkIndex.signatureById.set(nodeId, signature);
  emit({
    type: "chunk",
    nodeId,
    kind: "type_alias_declaration",
    name,
    namespace: ns,
    className: name,
    functionName: name,
    signature,
    filePath: rel,
    startLine: start,
    endLine: end,
    body: node.getText(),
    documentation: jsDocText(node),
    returnType: null,
    parameters: [],
    baseTypes: [],
    interfaces: [],
    modifiers: modifiersOf(node),
  });
}

function registerFunction(fn, rel, ns, fileClass, chunkIndex) {
  const name = fn.getName();
  if (!name) return;
  const start = fn.getStartLineNumber();
  const end = fn.getEndLineNumber();
  const params = formatParams(fn);
  const ret = returnTypeOf(fn);
  const signature = buildSignature(ns, null, name, params, ret);
  const nodeId = makeNodeId(rel, "function_declaration", name, start);

  chunkIndex.byNode.set(fn, nodeId);
  chunkIndex.signatureById.set(nodeId, signature);

  emit({
    type: "chunk",
    nodeId,
    kind: "function_declaration",
    name,
    namespace: ns,
    className: fileClass,
    functionName: name,
    signature,
    filePath: rel,
    startLine: start,
    endLine: end,
    body: fn.getText(),
    documentation: jsDocText(fn),
    returnType: ret,
    parameters: params,
    baseTypes: [],
    interfaces: [],
    modifiers: modifiersOf(fn),
  });
}

function registerArrowVarStatement(vs, rel, ns, fileClass, chunkIndex) {
  for (const decl of vs.getDeclarations()) {
    const init = decl.getInitializer();
    if (!init) continue;
    const k = init.getKind();
    if (k !== SyntaxKind.ArrowFunction && k !== SyntaxKind.FunctionExpression) continue;

    const name = decl.getName();
    const start = vs.getStartLineNumber();
    const end = decl.getEndLineNumber();
    const params = formatParams(init);
    const ret = returnTypeOf(init);
    const signature = buildSignature(ns, null, name, params, ret);
    const nodeId = makeNodeId(rel, "function_declaration", name, start);

    chunkIndex.byNode.set(decl, nodeId);
    chunkIndex.byNode.set(init, nodeId);
    chunkIndex.signatureById.set(nodeId, signature);

    emit({
      type: "chunk",
      nodeId,
      kind: "function_declaration",
      name,
      namespace: ns,
      className: fileClass,
      functionName: name,
      signature,
      filePath: rel,
      startLine: start,
      endLine: end,
      body: init.getText(),
      documentation: jsDocText(vs),
      returnType: ret,
      parameters: params,
      baseTypes: [],
      interfaces: [],
      modifiers: [...modifiersOf(vs), vs.getDeclarationKind()],
    });
  }
}

// ─── edge emission (second pass) ────────────────────────────────────────────

function emitCallEdges(sourceFiles, chunkIndex, pendingTypeEdges, project) {
  // Type-resolved inherits/implements edges
  for (const pending of pendingTypeEdges) {
    const sym = (() => {
      try {
        // Try the heritage clause expression's symbol.
        const heritage = pending.source.getHeritageClauses
          ? pending.source.getHeritageClauses()
          : [];
        for (const h of heritage) {
          for (const t of h.getTypeNodes()) {
            if (t.getText() === pending.targetText) {
              const sym = t.getExpression().getSymbol();
              if (sym) return sym.getAliasedSymbol() || sym;
            }
          }
        }
      } catch {}
      return null;
    })();

    let targetNodeId = null;
    let isExternal = true;
    if (sym) {
      const decls = sym.getDeclarations();
      const pick = decls && decls.find(
        (d) => !d.getSourceFile().isDeclarationFile() && !d.getSourceFile().isInNodeModules()
      );
      if (pick) {
        targetNodeId = chunkIndex.byNode.get(pick) || null;
        if (targetNodeId) isExternal = false;
      }
    }

    let extDoc = null, extPkg = null, extNs = null, extClass = null;
    if (isExternal && sym && project) {
      extDoc = getSymbolDoc(sym, project);
      const meta = getExternalSymbolMeta(sym);
      extNs = meta.namespace || null;
      extClass = meta.className || null;
      try {
        const sd = sym.getDeclarations();
        if (sd && sd.length > 0)
          extPkg = extractPackageFromPath(sd[0].getSourceFile().getFilePath());
      } catch {}
    }

    emit({
      type: "edge",
      sourceNodeId: pending.sourceId,
      targetNodeId,
      targetSignature: pending.targetText,
      targetName: pending.targetText,
      edgeKind: pending.kind,
      isExternal,
      filePath: chunkIndex.fileById.get(pending.sourceId) || null,
      lineNumber: pending.line,
      targetDocumentation: extDoc || undefined,
      targetAssembly: extPkg || undefined,
      targetNamespace: extNs || undefined,
      targetClassName: extClass || undefined,
    });
  }

  // Call + new expressions + JSX component usages
  for (const sf of sourceFiles) {
    sf.forEachDescendant((node) => {
      const kind = node.getKind();

      // ── regular calls / new ──────────────────────────────────────────────
      if (kind === SyntaxKind.CallExpression || kind === SyntaxKind.NewExpression) {
        const ownerId = findOwnerNodeId(node, chunkIndex);
        if (!ownerId) return;

        const expr = node.getExpression();
        const t = resolveCallTarget(expr, chunkIndex, project);
        const line = node.getStartLineNumber();

        emit({
          type: "edge",
          sourceNodeId: ownerId,
          targetNodeId: t.nodeId,
          targetSignature: t.signatureText,
          targetName: t.name,
          edgeKind: kind === SyntaxKind.NewExpression ? "creates" : "calls",
          isExternal: t.isExternal,
          filePath: chunkIndex.fileById.get(ownerId) || null,
          lineNumber: line,
          targetDocumentation: t.documentation || undefined,
          targetAssembly: t.packageName || undefined,
          targetNamespace: t.namespace || undefined,
          targetClassName: t.className || undefined,
        });
        return;
      }

      // ── JSX component usages: <Foo /> and <Foo>…</Foo> ──────────────────
      // Only handle the opening/self-closing element, not the closing tag
      // (which would produce a duplicate edge).
      if (kind === SyntaxKind.JsxSelfClosingElement || kind === SyntaxKind.JsxOpeningElement) {
        const tagNode = node.getTagNameNode ? node.getTagNameNode() : null;
        if (!tagNode) return;

        // Skip lowercase HTML intrinsics (div, span, input …).
        const tagText = tagNode.getText();
        if (!tagText || tagText[0] === tagText[0].toLowerCase()) return;

        const ownerId = findOwnerNodeId(node, chunkIndex);
        if (!ownerId) return;

        const t = resolveCallTarget(tagNode, chunkIndex, project);
        const line = node.getStartLineNumber();

        emit({
          type: "edge",
          sourceNodeId: ownerId,
          targetNodeId: t.nodeId,
          targetSignature: t.signatureText,
          targetName: t.name,
          edgeKind: "renders",
          isExternal: t.isExternal,
          filePath: chunkIndex.fileById.get(ownerId) || null,
          lineNumber: line,
          targetDocumentation: t.documentation || undefined,
          targetAssembly: t.packageName || undefined,
          targetNamespace: t.namespace || undefined,
          targetClassName: t.className || undefined,
        });
        return;
      }

      // ── JSX attribute symbol references: <Child propName={symbolRef} /> ─
      // Tracks which declared symbols flow into child components as props.
      // targetName  = the prop/attribute name  (→ TargetMemberName in C#)
      // targetSignature = the passed symbol's full signature
      if (kind === SyntaxKind.JsxAttribute) {
        const init = node.getInitializer ? node.getInitializer() : null;
        if (!init || init.getKind() !== SyntaxKind.JsxExpression) return;

        const inner = init.getExpression ? init.getExpression() : null;
        if (!inner) return;

        // Only track plain identifier and member-access references; skip
        // inline functions, ternaries, template literals, etc.
        const ik = inner.getKind();
        if (ik !== SyntaxKind.Identifier && ik !== SyntaxKind.PropertyAccessExpression) return;

        const ownerId = findOwnerNodeId(node, chunkIndex);
        if (!ownerId) return;

        const attrName = node.getNameNode ? node.getNameNode().getText() : null;
        const t = resolveCallTarget(inner, chunkIndex, project);
        const line = node.getStartLineNumber();

        emit({
          type: "edge",
          sourceNodeId: ownerId,
          targetNodeId: t.nodeId,
          targetSignature: t.signatureText,
          targetName: attrName || t.name,   // prop name → TargetMemberName
          edgeKind: "passes",
          isExternal: t.isExternal,
          filePath: chunkIndex.fileById.get(ownerId) || null,
          lineNumber: line,
          targetDocumentation: t.documentation || undefined,
          targetAssembly: t.packageName || undefined,
          targetNamespace: t.namespace || undefined,
          targetClassName: t.className || undefined,
        });
        return;
      }
    });
  }
}

// ─── analysis driver ────────────────────────────────────────────────────────

function analyzeAll(project, rootDir, opts) {
  const chunkIndex = {
    byNode: new Map(),
    signatureById: new Map(),
    fileById: new Map(),
  };
  const pendingTypeEdges = [];

  // Always include every project source file in the registration pass so
  // call/new expressions in the *changed* files can still resolve their
  // targets in *unchanged* files. Without this, every cross-file edge after
  // an incremental reanalyze would degrade to `isExternal=true`.
  const includeForRegistration = (sf) => {
    if (sf.isDeclarationFile()) return false;
    if (sf.isInNodeModules()) return false;
    return true;
  };

  const allSourceFiles = project.getSourceFiles().filter(includeForRegistration);

  // Configure the per-call emit filter from opts.files, if any. Records for
  // files outside this set are silently dropped by emit().
  const filterSet = (opts && opts.files && opts.files.size > 0) ? opts.files : null;
  setEmitFilter(filterSet);
  emitLog(`analyzing ${allSourceFiles.length} source files` +
    (filterSet ? ` (emit filter: ${filterSet.size} files)` : ""));

  try {
    // First pass: register and emit chunks (emit suppressed for files outside filter).
    for (const sf of allSourceFiles) {
      try {
        const rel = relPath(rootDir, sf);
        visitSourceFile(sf, rootDir, chunkIndex, pendingTypeEdges);
        for (const [, id] of chunkIndex.byNode) {
          if (!chunkIndex.fileById.has(id)) {
            chunkIndex.fileById.set(id, rel);
          }
        }
      } catch (e) {
        emitError(`Failed processing ${sf.getFilePath()}`, e.stack || e.message);
      }
    }

    // Second pass: edges (calls, new, type heritage). The emit filter still
    // applies, so only edges originating in changed files are written.
    try {
      emitCallEdges(allSourceFiles, chunkIndex, pendingTypeEdges, project);
    } catch (e) {
      emitError("Edge extraction failed", e.stack || e.message);
    }
  } finally {
    setEmitFilter(null);
  }
}

// ─── CLI / server entry ─────────────────────────────────────────────────────

function parseArgs(argv) {
  const args = { server: false, project: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--server") args.server = true;
    else if (a === "--project" || a === "-p") args.project = argv[++i];
    else if (a.startsWith("--project=")) args.project = a.slice("--project=".length);
  }
  return args;
}

async function runBatch(projectInput) {
  if (!projectInput) {
    emitError("--project <path> is required in batch mode");
    process.exit(64);
  }
  const { project, rootDir } = loadProject(projectInput);
  analyzeAll(project, rootDir, null);
  emit({ type: "done" });
}

function runServer() {
  let project = null;
  let rootDir = null;
  let buffer = "";

  process.stdin.setEncoding("utf8");
  process.stdin.on("data", (chunk) => {
    buffer += chunk;
    let nl;
    while ((nl = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, nl).trim();
      buffer = buffer.slice(nl + 1);
      if (!line) continue;
      handleRequest(line);
    }
  });
  process.stdin.on("end", () => process.exit(0));

  function handleRequest(line) {
    let req;
    try { req = JSON.parse(line); } catch (e) {
      emitError("Invalid JSON request", line);
      // No sentinel possible without a parsed op — but the C# side only
      // serializes valid requests, so this branch is informational.
      return;
    }
    // The contract with the C# host is: every request gets exactly one
    // terminating envelope (`opened` for open, `done` for analyze/reanalyze).
    // If we throw mid-way and only emit `error`, the host's SendAsync will
    // wait forever for the sentinel — so always emit one in a finally block.
    const terminator =
      req.op === "open" ? { type: "opened" } :
      (req.op === "analyze" || req.op === "reanalyze") ? { type: "done" } :
      null;
    let sentTerminator = false;
    const sendTerminator = (extra) => {
      if (sentTerminator || !terminator) return;
      sentTerminator = true;
      emit(extra ? Object.assign({}, terminator, extra) : terminator);
    };
    try {
      switch (req.op) {
        case "open": {
          const loaded = loadProject(req.project || req.root);
          project = loaded.project;
          rootDir = loaded.rootDir;
          sendTerminator({ rootDir });
          break;
        }
        case "analyze": {
          if (!project) { emitError("No project opened"); sendTerminator(); return; }
          analyzeAll(project, rootDir, null);
          sendTerminator();
          break;
        }
        case "reanalyze": {
          if (!project) { emitError("No project opened"); sendTerminator(); return; }
          // Refresh changed files from disk so ts-morph re-parses them.
          // Files can arrive as absolute paths or rel-to-rootDir; normalize
          // both, and try forward-slash variants too since ts-morph
          // canonicalizes its internal source-file paths to forward slashes
          // and getSourceFile() compares exactly.
          const incoming = (req.files || []).map((f) => {
            if (path.isAbsolute(f)) return path.resolve(f);
            return path.resolve(rootDir, f);
          });
          for (const f of incoming) {
            try {
              const fwd = f.replace(/\\/g, "/");
              const sf = project.getSourceFile(f) || project.getSourceFile(fwd);
              if (sf) sf.refreshFromFileSystemSync();
              else project.addSourceFileAtPathIfExists(f);
            } catch (e) {
              emitError(`refresh failed for ${f}`, e.stack || e.message);
            }
          }
          // Build the emit filter as a set of rel paths with forward slashes,
          // matching what emit() compares against.
          const rels = new Set(
            incoming.map((f) => path.relative(rootDir, f).replace(/\\/g, "/"))
          );
          analyzeAll(project, rootDir, { files: rels });
          sendTerminator();
          break;
        }
        case "close":
        case "shutdown":
          process.exit(0);
          break;
        default:
          emitError(`Unknown op: ${req.op}`);
      }
    } catch (e) {
      emitError("Request handler failed", e.stack || e.message);
    } finally {
      // Belt-and-braces: never let the host hang waiting for a sentinel.
      sendTerminator();
    }
  }
}

const args = parseArgs(process.argv.slice(2));
if (args.server) runServer();
else runBatch(args.project).catch((e) => {
  emitError("Batch run failed", e.stack || e.message);
  process.exit(1);
});
