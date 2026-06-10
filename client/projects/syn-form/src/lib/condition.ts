import { Condition } from './types';

/** Client mirror of the server's ConditionEvaluator — same closed operator set, same semantics. */
export function evaluateCondition(cond: Condition, values: Record<string, unknown>): boolean {
  if (cond.and?.length) return cond.and.every(c => evaluateCondition(c, values));
  if (cond.or?.length) return cond.or.some(c => evaluateCondition(c, values));

  const left = cond.field ? values[cond.field] : undefined;
  const right = cond.valueFromField ? values[cond.valueFromField] : cond.value;

  switch (cond.op) {
    case 'notEmpty': return !isEmpty(left);
    case 'eq': return looseEquals(left, right);
    case 'neq': return !looseEquals(left, right);
    case 'in': return Array.isArray(right) && right.some(r => looseEquals(left, r));
    case 'contains': return Array.isArray(left) && left.some(l => looseEquals(l, right));
    case 'gt': return compare(left, right) !== null && compare(left, right)! > 0;
    case 'lt': return compare(left, right) !== null && compare(left, right)! < 0;
    case 'gte': return compare(left, right) !== null && compare(left, right)! >= 0;
    case 'lte': return compare(left, right) !== null && compare(left, right)! <= 0;
    default: return false;
  }
}

export function referencedFields(cond: Condition): string[] {
  const fields: string[] = [];
  if (cond.and) cond.and.forEach(c => fields.push(...referencedFields(c)));
  if (cond.or) cond.or.forEach(c => fields.push(...referencedFields(c)));
  if (cond.field) fields.push(cond.field);
  if (cond.valueFromField) fields.push(cond.valueFromField);
  return [...new Set(fields)];
}

export function isEmpty(v: unknown): boolean {
  return v == null
    || (typeof v === 'string' && v.trim() === '')
    || (Array.isArray(v) && v.length === 0);
}

function looseEquals(a: unknown, b: unknown): boolean {
  if (a == null || b == null) return false;
  return comparableString(a) === comparableString(b);
}

function comparableString(v: unknown): string {
  if (typeof v === 'string') return v.trim().toLowerCase();
  return String(v);
}

/** Numeric when both parse as numbers, else ordinal string compare (ISO dates/times sort correctly). */
function compare(a: unknown, b: unknown): number | null {
  if (isEmpty(a) || isEmpty(b)) return null;
  const na = Number(a), nb = Number(b);
  if (!Number.isNaN(na) && !Number.isNaN(nb) && a !== '' && b !== '') return na - nb;
  if (typeof a === 'string' && typeof b === 'string') return a < b ? -1 : a > b ? 1 : 0;
  return null;
}
