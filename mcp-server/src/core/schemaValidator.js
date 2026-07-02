/**
 * Minimal JSON-Schema validator for the catalog's param-schema subset: `type` (single or a
 * union array), `required`, `properties` (recursed), `enum`, `items` (recursed), nested
 * objects/arrays, and `additionalProperties: false`. Returns precise, field-pathed, human-readable
 * errors so a model can self-correct a generic `call_unity_tool` payload — the Node side is the
 * sole call-time gate for the generic surface (ADR 0004), since the MCP client only sees the opaque
 * generic envelope.
 *
 * Deliberately hand-rolled and dependency-free (not ajv): the schema surface is the bounded set the
 * catalog uses, this keeps the npx-friendly single-runtime-dependency posture, and it gives full
 * control over the error wording. Unknown keywords (`description`, `default`, `$comment`,
 * `oneOf`/`anyOf`, …) are ignored rather than rejected.
 */

function typeName(value) {
  if (value === null) return 'null';
  if (Array.isArray(value)) return 'array';
  return typeof value;
}

function matchesType(value, type) {
  switch (type) {
    case 'object': return value !== null && typeof value === 'object' && !Array.isArray(value);
    case 'array': return Array.isArray(value);
    case 'string': return typeof value === 'string';
    case 'number': return typeof value === 'number' && Number.isFinite(value);
    case 'integer': return typeof value === 'number' && Number.isInteger(value);
    case 'boolean': return typeof value === 'boolean';
    case 'null': return value === null;
    default: return true; // unknown type keyword — don't fail on it
  }
}

function join(path, key) {
  if (typeof key === 'number') return `${path}[${key}]`;
  return path ? `${path}.${key}` : key;
}

function walk(value, schema, path, errors) {
  if (!schema || typeof schema !== 'object') return; // no schema -> accept anything

  if (schema.type !== undefined) {
    const types = Array.isArray(schema.type) ? schema.type : [schema.type];
    if (!types.some((t) => matchesType(value, t))) {
      errors.push(`${path || 'value'}: expected ${types.join(' or ')}, got ${typeName(value)}`);
      return; // wrong type — deeper checks would just be noise
    }
  }

  if (Array.isArray(schema.enum) && !schema.enum.some((e) => e === value)) {
    errors.push(`${path || 'value'}: must be one of ${JSON.stringify(schema.enum)}, got ${JSON.stringify(value)}`);
  }

  // Numeric range, string length/pattern, array length — the catalog uses minimum/maximum/pattern
  // (the sole call_unity_tool gate silently ignored them before). (Audit finding.)
  const where = path || 'value';
  if (typeof value === 'number') {
    if (typeof schema.minimum === 'number' && value < schema.minimum) errors.push(`${where}: must be >= ${schema.minimum}, got ${value}`);
    if (typeof schema.maximum === 'number' && value > schema.maximum) errors.push(`${where}: must be <= ${schema.maximum}, got ${value}`);
  }
  if (typeof value === 'string') {
    if (typeof schema.minLength === 'number' && value.length < schema.minLength) errors.push(`${where}: must be at least ${schema.minLength} characters`);
    if (typeof schema.maxLength === 'number' && value.length > schema.maxLength) errors.push(`${where}: must be at most ${schema.maxLength} characters`);
    if (typeof schema.pattern === 'string') {
      let re = null;
      try { re = new RegExp(schema.pattern); } catch { /* invalid pattern in schema -> skip */ }
      if (re && !re.test(value)) errors.push(`${where}: must match pattern ${schema.pattern}`);
    }
  }
  if (Array.isArray(value)) {
    if (typeof schema.minItems === 'number' && value.length < schema.minItems) errors.push(`${where}: must have at least ${schema.minItems} item(s)`);
    if (typeof schema.maxItems === 'number' && value.length > schema.maxItems) errors.push(`${where}: must have at most ${schema.maxItems} item(s)`);
  }

  if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
    if (Array.isArray(schema.required)) {
      for (const req of schema.required) {
        // Own-property AND not undefined: catches an absent key, a key explicitly set to undefined
        // (JSON.stringify drops it from the wire), and inherited names like "toString". (Audit.)
        if (!Object.prototype.hasOwnProperty.call(value, req) || value[req] === undefined) {
          errors.push(`${path || 'value'}: missing required property "${req}"`);
        }
      }
    }
    if (schema.properties && typeof schema.properties === 'object') {
      for (const [key, sub] of Object.entries(schema.properties)) {
        if (value[key] !== undefined) walk(value[key], sub, join(path, key), errors);
      }
    }
    if (schema.additionalProperties === false) {
      // Do NOT require a sibling `properties`: {type:'object', additionalProperties:false} means "no properties
      // allowed", so an empty allow-set must reject ANY key (previously it silently accepted everything). (Node-12.)
      const allowed = new Set(schema.properties ? Object.keys(schema.properties) : []);
      for (const key of Object.keys(value)) {
        if (!allowed.has(key)) errors.push(`${join(path, key)}: unknown property (additionalProperties is false)`);
      }
    }
  }

  if (Array.isArray(value) && schema.items && typeof schema.items === 'object') {
    for (let i = 0; i < value.length; i++) walk(value[i], schema.items, join(path, i), errors);
  }
}

/**
 * Validate a value against a JSON-Schema subset.
 * @param {*} value
 * @param {object} schema
 * @param {string} [rootPath] label for the top-level value in error messages (e.g. 'params')
 * @returns {{ valid: boolean, errors: string[] }}
 */
export function validateAgainstSchema(value, schema, rootPath = '') {
  const errors = [];
  walk(value, schema, rootPath, errors);
  return { valid: errors.length === 0, errors };
}
