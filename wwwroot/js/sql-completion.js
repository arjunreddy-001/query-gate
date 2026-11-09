(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.queryGateCompletion = api;
})(typeof globalThis === "object" ? globalThis : this, function () {
  "use strict";

  const reserved = new Set(("select from where join inner left right full outer cross apply on as group order having union except intersect set values update delete insert into with by when then else end and or for offset fetch option output returning").split(" "));
  const identifier = (t) => t && (t.kind === "word" || t.kind === "identifier");
  const name = (t) => (t?.value || "").toLowerCase();
  const quote = (s) => /^(?:[a-z_][a-z0-9_@$#]*|[@#]{1,2}[a-z_][a-z0-9_@$#]*)$/i.test(s) && !reserved.has(s.toLowerCase()) ? s : "[" + s.replace(/]/g, "]]") + "]";

  function tokenize(sql) {
    const tokens = [];
    let i = 0;
    while (i < sql.length) {
      const start = i, c = sql[i], next = sql[i + 1];
      if (/\s/.test(c)) { i++; continue; }
      if (c === "-" && next === "-") {
        i += 2; while (i < sql.length && sql[i] !== "\n") i++;
        tokens.push({ kind: "comment", start, end: i, unclosed: i === sql.length }); continue;
      }
      if (c === "/" && next === "*") {
        i += 2; let depth = 1;
        while (i < sql.length && depth) {
          if (sql[i] === "/" && sql[i + 1] === "*") { depth++; i += 2; }
          else if (sql[i] === "*" && sql[i + 1] === "/") { depth--; i += 2; }
          else i++;
        }
        tokens.push({ kind: "comment", start, end: i, unclosed: depth > 0 }); continue;
      }
      if (c === "'" || c === "[" || c === '"' || c === "`") {
        const close = c === "[" ? "]" : c;
        i++; let value = "", closed = false;
        while (i < sql.length) {
          if (sql[i] === close) {
            if (sql[i + 1] === close) { value += close; i += 2; continue; }
            i++; closed = true; break;
          }
          value += sql[i++];
        }
        tokens.push({ kind: c === "'" ? "string" : "identifier", value, start, end: i, unclosed: !closed }); continue;
      }
      if (/[a-z_@#$]/i.test(c)) {
        i++; while (i < sql.length && /[\w@$#]/.test(sql[i])) i++;
        tokens.push({ kind: "word", value: sql.slice(start, i), start, end: i }); continue;
      }
      i++; tokens.push({ kind: "symbol", value: c, start, end: i });
    }
    return tokens;
  }

  function analyze(sql, offset, metadata) {
    const all = tokenize(sql);
    if (all.some(t => (t.kind === "comment" || t.kind === "string") &&
      t.start < offset && (offset < t.end || (offset === t.end && (t.unclosed || t.kind === "comment"))))) return { context: "none", suggestions: [] };
    const separators = all.filter(t => t.kind === "word" && name(t) === "go" &&
      /^\s*$/m.test(sql.slice(sql.lastIndexOf("\n", t.start - 1) + 1, t.start)) &&
      /^\s*(?:--[^\n]*)?$/.test(sql.slice(t.end, sql.indexOf("\n", t.end) < 0 ? sql.length : sql.indexOf("\n", t.end))));
    let batchStart = 0, batchEnd = sql.length;
    for (const t of separators) { if (t.end <= offset) batchStart = t.end; else if (t.start > offset) { batchEnd = t.start; break; } }
    const ts = all.filter(t => t.start >= batchStart && t.start < batchEnd && t.kind !== "comment" && t.kind !== "string");
    const scopes = [{ start: batchStart, end: batchEnd, parent: null, depth: 0 }];
    const stack = [scopes[0]];
    for (const t of ts) {
      t.scope = stack.at(-1);
      if (t.value === "(") { const s = { start: t.start, end: batchEnd, parent: stack.at(-1), depth: stack.length }; scopes.push(s); stack.push(s); }
      else if (t.value === ")" && stack.length > 1) { stack.pop().end = t.end; t.scope = stack.at(-1); }
    }
    const isQuery = (s) => s === scopes[0] || ts.some(t => t.scope === s && t.kind === "word" && name(t) === "select");
    let current = scopes.filter(s => s.start < offset && offset <= s.end && isQuery(s)).sort((a, b) => b.depth - a.depth)[0] || scopes[0];
    const statementStart = ts.filter(t => t.scope === current && t.value === ";" && t.end <= offset).at(-1)?.end ?? current.start;
    const statementEnd = ts.find(t => t.scope === current && t.value === ";" && t.start >= offset)?.start ?? current.end;
    const tokens = ts.filter(t => t.scope === current && t.start >= statementStart && t.start < statementEnd);
    const objects = metadata || [];
    const declaredTables = [];
    const variables = [];
    for (let i = 0; i < ts.length; i++) {
      const t = ts[i];
      if (t.start >= offset) break;
      if (name(t) === "declare" && identifier(ts[i + 1]) && name(ts[i + 1]).startsWith("@")) {
        variables.push(ts[i + 1].value);
        if (name(ts[i + 2]) === "table" && ts[i + 3]?.value === "(") {
          const definition = scopes.find(s => s.start === ts[i + 3].start);
          if (definition) declaredTables.push({ schema: "", name: ts[i + 1].value, type: "Temporary", columns: definitionColumns(definition) });
        }
      }
      if (name(t) === "create" && name(ts[i + 1]) === "table" && name(ts[i + 2]).startsWith("#") && ts[i + 3]?.value === "(") {
        const definition = scopes.find(s => s.start === ts[i + 3].start);
        if (definition) declaredTables.push({ schema: "", name: ts[i + 2].value, type: "Temporary", columns: definitionColumns(definition) });
      }
    }
    function definitionColumns(scope) {
      const own = ts.filter(t => t.scope === scope);
      const groups = [[]];
      for (const t of own) { if (t.value === ",") groups.push([]); else groups.at(-1).push(t); }
      return groups.filter(g => identifier(g[0]) && identifier(g[1]) &&
        !["constraint", "primary", "unique", "foreign", "check"].includes(name(g[0]))).map(g => g[0].value);
    }
    const lookup = (parts) => {
      if (parts.length > 2) return null;
      const hits = [...objects, ...declaredTables].filter(o => o.name.toLowerCase() === parts.at(-1) &&
        (parts.length === 1 || o.schema.toLowerCase() === parts.at(-2)));
      return hits.length === 1 ? hits[0] : null;
    };
    function projection(scope, depth) {
      if (depth > 8) return [];
      const own = ts.filter(t => t.scope === scope);
      const start = own.findIndex(t => name(t) === "select");
      if (start < 0) return [];
      let end = own.findIndex((t, i) => i > start && name(t) === "from");
      if (end < 0) end = own.length;
      const groups = [[]];
      for (const t of own.slice(start + 1, end)) {
        if (t.value === ",") groups.push([]); else groups.at(-1).push(t);
      }
      const cols = [];
      for (const group of groups) {
        const as = group.findIndex(t => name(t) === "as");
        if (as >= 0 && identifier(group[as + 1])) cols.push(group[as + 1].value);
        else if (group.length > 1 && identifier(group.at(-1)) && group.at(-2).value !== ".") cols.push(group.at(-1).value);
        else if (identifier(group.at(-1))) cols.push(group.at(-1).value);
        else if (group.at(-1)?.value === "*") {
          const inner = sources(scope, scope.start, scope.end, depth + 1);
          const qualifier = group.length > 1 && group.at(-2).value === "." ? name(group.at(-3)) : null;
          for (const source of inner.filter(s => !qualifier || s.alias.toLowerCase() === qualifier))
            for (const c of source.columns) cols.push(typeof c === "string" ? c : c.name);
        }
      }
      return [...new Set(cols)];
    }
    function sources(scope, start, end, depth = 0) {
      if (depth > 8) return [];
      const own = ts.filter(t => t.scope === scope && t.start >= start && t.start < end);
      const found = [];
      for (let i = 0; i < own.length; i++) {
        const t = own[i];
        if (t.kind !== "word" || !["from", "join", "apply", "update", "into"].includes(name(t))) continue;
        let j = i + 1;
        if (own[j]?.value === "(" && name(t) !== "into") {
          const child = scopes.find(s => s.start === own[j].start && s.parent === scope);
          if (!child || !isQuery(child)) continue;
          j = own.findIndex((token, index) => index > j && token.value === ")" && token.end === child.end);
          if (j < 0) continue;
          j++;
          if (name(own[j]) === "as") j++;
          if (!identifier(own[j]) || reserved.has(name(own[j]))) continue;
          const alias = own[j].value;
          found.push({ alias, object: { schema: "", name: alias }, columns: projection(child, depth + 1) });
          continue;
        }
        if (!identifier(own[j])) continue;
        const parts = [name(own[j++])];
        while (own[j]?.value === "." && identifier(own[j + 1])) { parts.push(name(own[j + 1])); j += 2; }
        if (own[j]?.value === "(" && name(t) !== "into") {
          const args = scopes.find(s => s.start === own[j].start && s.parent === scope);
          if (!args) continue;
          const close = own.findIndex((token, index) => index > j && token.value === ")" && token.end === args.end);
          if (close < 0) continue;
          j = close + 1;
        }
        if (own[j] && name(own[j]) === "as") j++;
        const alias = identifier(own[j]) && !reserved.has(name(own[j])) ? own[j].value : parts.at(-1);
        let object = lookup(parts);
        if (!object && parts.length === 1) {
          const withIndex = own.findIndex(token => name(token) === "with");
          if (withIndex >= 0) {
            for (let k = withIndex + 1; k < i - 2; k++) {
              if (!identifier(own[k]) || name(own[k]) !== parts[0] || name(own[k + 1]) !== "as" || own[k + 2]?.value !== "(") continue;
              const child = scopes.find(s => s.start === own[k + 2].start && s.parent === scope);
              if (child && isQuery(child)) object = { schema: "", name: own[k].value, columns: projection(child, depth + 1) };
            }
          }
        }
        if (object && !found.some(s => s.alias.toLowerCase() === alias.toLowerCase()))
          found.push({ alias, object, columns: object.columnDetails?.length ? object.columnDetails : object.columns || [] });
      }
      return found;
    }
    const visible = sources(current, statementStart, statementEnd);
    for (let parent = current.parent; parent; parent = parent.parent) {
      if (!isQuery(parent)) continue;
      const start = ts.filter(t => t.scope === parent && t.value === ";" && t.end <= offset).at(-1)?.end ?? parent.start;
      const end = ts.find(t => t.scope === parent && t.value === ";" && t.start >= offset)?.start ?? parent.end;
      for (const s of sources(parent, start, end))
        if (!visible.some(v => v.alias.toLowerCase() === s.alias.toLowerCase())) visible.push(s);
    }
    const before = tokens.filter(t => t.end <= offset);
    const partial = all.find(t => identifier(t) && t.start < offset && t.end >= offset);
    const replacement = partial?.kind === "identifier" ? { start: partial.start, end: partial.end } : null;
    const prior = before.filter(t => t !== partial);
    const dot = prior.at(-1)?.value === "." ? prior.at(-2) : null;
    const prev = prior.at(-1);
    const sourceContext = prev && prev.kind === "word" && ["from", "join", "apply", "update", "into"].includes(name(prev));
    const suggestions = [];
    const addColumn = (source, c, qualified) => {
      const col = typeof c === "string" ? { name: c } : c;
      const ambiguous = visible.filter(s => s.columns.some(x => (typeof x === "string" ? x : x.name).toLowerCase() === col.name.toLowerCase())).length > 1;
      suggestions.push({ label: col.name, insertText: (qualified || !ambiguous ? "" : quote(source.alias) + ".") + quote(col.name),
        kind: "column", detail: source.alias + " / " + source.object.schema + "." + source.object.name +
          (col.dataType ? " · " + col.dataType + (col.nullable ? " NULL" : " NOT NULL") : "") });
    };
    if (identifier(dot)) {
      const source = visible.find(s => s.alias.toLowerCase() === name(dot));
      if (source) source.columns.forEach(c => addColumn(source, c, true));
      else if (sourceContext || (prior.length >= 3 && ["from", "join", "apply", "update", "into"].includes(name(prior.at(-3) || {}))))
        objects.filter(o => o.schema.toLowerCase() === name(dot) && (["Table", "View"].includes(o.type) || o.isTableValuedFunction))
          .forEach(o => suggestions.push({ label: o.name, insertText: quote(o.name), kind: "object", detail: o.type }));
      return { context: "qualified", suggestions, replacement };
    }
    if (sourceContext) {
      objects.filter(o => ["Table", "View"].includes(o.type) || o.isTableValuedFunction)
        .forEach(o => suggestions.push({ label: o.schema + "." + o.name, insertText: quote(o.schema) + "." + quote(o.name), kind: "object", detail: o.type }));
      declaredTables.forEach(o => suggestions.push({ label: o.name, insertText: o.name, kind: "object", detail: "Temporary table" }));
      const withIndex = tokens.findIndex(t => name(t) === "with");
      if (withIndex >= 0) for (let i = withIndex + 1; i < tokens.length - 2; i++) {
        if (identifier(tokens[i]) && name(tokens[i + 1]) === "as" && tokens[i + 2].value === "(") {
          const child = scopes.find(s => s.start === tokens[i + 2].start && s.parent === current);
          if (child && child.end <= offset && isQuery(child))
            suggestions.push({ label: tokens[i].value, insertText: quote(tokens[i].value), kind: "object", detail: "CTE" });
        }
      }
      return { context: "source", suggestions, replacement };
    }
    if (name(prev) === "on") {
      const joined = visible.at(-1);
      if (joined) for (const source of visible) {
        for (const fk of source.object.foreignKeys || []) {
          for (const target of visible) {
            if (source === target || (source !== joined && target !== joined) ||
                target.object.schema.toLowerCase() !== fk.referencedSchema.toLowerCase() ||
                target.object.name.toLowerCase() !== fk.referencedTable.toLowerCase()) continue;
            const condition = fk.columns.map(c => quote(source.alias) + "." + quote(c.column) +
              " = " + quote(target.alias) + "." + quote(c.referencedColumn)).join(" AND ");
            if (condition) suggestions.push({ label: fk.name + " (" + source.alias + " → " + target.alias + ")",
              insertText: condition, kind: "join", detail: condition });
          }
        }
      }
    }
    visible.forEach(s => s.columns.forEach(c => addColumn(s, c, false)));
    variables.forEach(v => suggestions.push({ label: v, insertText: v, kind: "variable", detail: "SQL variable" }));
    return { context: "expression", suggestions, replacement };
  }
  return { analyze, tokenize };
});
