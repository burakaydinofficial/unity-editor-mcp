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

  if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
    if (Array.isArray(schema.required)) {
      for (const req of schema.required) {
        if (!(req in value)) errors.push(`${path || 'value'}: missing required property "${req}"`);
      }
    }
    if (schema.properties && typeof schema.properties === 'object') {
      for (const [key, sub] of Object.entries(schema.properties)) {
        if (value[key] !== undefined) walk(value[key], sub, join(path, key), errors);
      }
    }
    if (schema.additionalProperties === false && schema.properties) {
      const allowed = new Set(Object.keys(schema.properties));
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
