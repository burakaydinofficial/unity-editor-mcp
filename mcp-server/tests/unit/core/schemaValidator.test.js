import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { validateAgainstSchema } from '../../../src/core/schemaValidator.js';

const ok = (r) => assert.equal(r.valid, true, 'expected valid; errors: ' + JSON.stringify(r.errors));
const bad = (r, re) => {
  assert.equal(r.valid, false, 'expected invalid');
  if (re) assert.ok(r.errors.some((e) => re.test(e)), 'errors: ' + JSON.stringify(r.errors));
};

describe('validateAgainstSchema', () => {
  it('accepts a matching object with required + typed props', () => {
    const schema = { type: 'object', properties: { name: { type: 'string' }, count: { type: 'number' } }, required: ['name'] };
    ok(validateAgainstSchema({ name: 'x', count: 3 }, schema));
    ok(validateAgainstSchema({ name: 'x' }, schema)); // count optional
  });

  it('reports a missing required property by name', () => {
    const schema = { type: 'object', properties: { name: { type: 'string' } }, required: ['name'] };
    bad(validateAgainstSchema({}, schema), /required.*name/i);
  });

  it('reports a type mismatch with path + expected/got', () => {
    const schema = { type: 'object', properties: { radius: { type: 'number' } } };
    bad(validateAgainstSchema({ radius: 'big' }, schema), /radius.*number/);
  });

  it('checks each primitive type', () => {
    ok(validateAgainstSchema('s', { type: 'string' }));
    ok(validateAgainstSchema(3, { type: 'number' }));
    ok(validateAgainstSchema(3, { type: 'integer' }));
    bad(validateAgainstSchema(3.5, { type: 'integer' }), /integer/);
    ok(validateAgainstSchema(true, { type: 'boolean' }));
    ok(validateAgainstSchema(null, { type: 'null' }));
    ok(validateAgainstSchema([], { type: 'array' }));
    ok(validateAgainstSchema({}, { type: 'object' }));
    bad(validateAgainstSchema([], { type: 'object' }), /object/); // array is not object
    bad(validateAgainstSchema(null, { type: 'object' }), /object/); // null is not object
  });

  it('supports a union type array (["number","null"])', () => {
    const schema = { type: ['number', 'null'] };
    ok(validateAgainstSchema(5, schema));
    ok(validateAgainstSchema(null, schema));
    bad(validateAgainstSchema('x', schema), /number.*null|null.*number/);
  });

  it('enforces enum membership', () => {
    const schema = { type: 'string', enum: ['a', 'b'] };
    ok(validateAgainstSchema('a', schema));
    bad(validateAgainstSchema('c', schema), /one of/i);
  });

  it('recurses into array items with indexed paths', () => {
    const schema = { type: 'array', items: { type: 'number' } };
    ok(validateAgainstSchema([1, 2, 3], schema));
    bad(validateAgainstSchema([1, 'two', 3], schema), /\[1\].*number/);
  });

  it('recurses into nested object properties with dotted paths', () => {
    const schema = { type: 'object', properties: { pos: { type: 'object', properties: { x: { type: 'number' } }, required: ['x'] } } };
    ok(validateAgainstSchema({ pos: { x: 1 } }, schema));
    bad(validateAgainstSchema({ pos: { x: 'a' } }, schema), /pos\.x.*number/);
    bad(validateAgainstSchema({ pos: {} }, schema), /pos.*required.*x/i);
  });

  it('allows unknown properties by default (lenient)', () => {
    const schema = { type: 'object', properties: { a: { type: 'string' } } };
    ok(validateAgainstSchema({ a: 'x', extra: 1 }, schema));
  });

  it('rejects unknown properties when additionalProperties is false', () => {
    const schema = { type: 'object', properties: { a: { type: 'string' } }, additionalProperties: false };
    bad(validateAgainstSchema({ a: 'x', extra: 1 }, schema), /unknown|additional/i);
  });

  it('treats an empty/missing schema as accept-anything', () => {
    ok(validateAgainstSchema({ anything: true }, {}));
    ok(validateAgainstSchema(42, undefined));
  });

  it('collects multiple errors at once', () => {
    const schema = { type: 'object', properties: { a: { type: 'string' }, b: { type: 'number' } }, required: ['a', 'b'] };
    const r = validateAgainstSchema({ a: 1 }, schema); // a wrong type + b missing
    assert.equal(r.valid, false);
    assert.ok(r.errors.length >= 2, 'errors: ' + JSON.stringify(r.errors));
  });

  it('flags an absent required key', () => {
    const schema = { type: 'object', properties: { name: { type: 'string' } }, required: ['name'] };
    ok(validateAgainstSchema({ name: 'x' }, schema));
    bad(validateAgainstSchema({ other: 1 }, schema), /required.*name/i);
  });

  it('flags a required key whose value is undefined (JSON.stringify would drop it from the wire)', () => {
    const schema = { type: 'object', properties: { name: { type: 'string' } }, required: ['name'] };
    bad(validateAgainstSchema({ name: undefined }, schema), /required.*name/i);
  });

  it('flags a required inherited-name property — no prototype-chain bypass', () => {
    const schema = { type: 'object', properties: {}, required: ['toString'] };
    bad(validateAgainstSchema({}, schema), /required.*toString/i);
  });

  it('a null value for a typed required key fails the type check', () => {
    const schema = { type: 'object', properties: { name: { type: 'string' } }, required: ['name'] };
    bad(validateAgainstSchema({ name: null }, schema), /name.*string/);
  });

  it('enforces minimum and maximum', () => {
    const schema = { type: 'number', minimum: 1, maximum: 10 };
    ok(validateAgainstSchema(5, schema));
    bad(validateAgainstSchema(0, schema), />= ?1/);
    bad(validateAgainstSchema(11, schema), /<= ?10/);
  });

  it('enforces string length and pattern', () => {
    ok(validateAgainstSchema('abc', { type: 'string', minLength: 2, maxLength: 5 }));
    bad(validateAgainstSchema('a', { type: 'string', minLength: 2 }), /at least 2/);
    bad(validateAgainstSchema('toolong', { type: 'string', maxLength: 3 }), /at most 3/);
    ok(validateAgainstSchema('AB12', { type: 'string', pattern: '^[A-Z0-9]+$' }));
    bad(validateAgainstSchema('ab', { type: 'string', pattern: '^[A-Z0-9]+$' }), /pattern/);
  });
});
