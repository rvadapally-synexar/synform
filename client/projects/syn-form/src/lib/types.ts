/** The layout JSON contract — mirrors the server's LayoutModels (single source of truth, spec §4). */

export type ControlType =
  | 'text' | 'textarea' | 'number' | 'dropdown' | 'multiselect' | 'radio'
  | 'checkbox' | 'checkboxGroup' | 'date' | 'time' | 'bpPair';

export type DataType = 'string' | 'number' | 'boolean' | 'date' | 'time' | 'string[]' | 'bpPair';

export type ConditionOp = 'eq' | 'neq' | 'in' | 'contains' | 'gt' | 'lt' | 'gte' | 'lte' | 'notEmpty';

export interface Condition {
  field?: string;
  op?: ConditionOp;
  value?: unknown;
  valueFromField?: string;
  and?: Condition[];
  or?: Condition[];
}

export interface OptionItem { value: string; label: string; }

export interface FieldDef {
  name: string;
  label: string;
  controlType: ControlType;
  dataType: DataType;
  required?: boolean;
  mustBeTrue?: boolean;
  options?: { inline?: OptionItem[]; lookupKey?: string };
  group?: string;
  order?: number;
  unit?: string | null;
  min?: number | string | null;
  max?: number | string | null;
  pattern?: string | null;
  bpBounds?: { sysMin: number; sysMax: number; diaMin: number; diaMax: number };
  aliases?: string[];
  visibleWhen?: Condition | null;
  enabledWhen?: Condition | null;
  requiredWhen?: Condition | null;
  computedFrom?: { fn: string; inputs: string[] } | null;
  filterBy?: string | null;
}

export interface CrossFieldRule { id: string; message: string; condition: Condition; }

export interface LayoutDef {
  title?: string;
  fields: FieldDef[];
  crossFieldRules?: CrossFieldRule[];
}

export interface LayoutEnvelope {
  layoutKey: string;
  version: number;
  status: 'draft' | 'published';
  updatedAt: string;
  publishedAt?: string;
  json: LayoutDef;
}

export interface LayoutSummary {
  layoutKey: string;
  title?: string;
  latestPublishedVersion?: number | null;
  draftVersion?: number | null;
  publishedAt?: string | null;
}

export interface LookupItem {
  id: string;
  lookupKey: string;
  value: string;
  label: string;
  parentKey?: string | null;
  sortOrder: number;
  active: boolean;
}

export type PopulateSource = 'voice' | 'photo' | 'agent' | 'manual';

export interface ProvenanceEntry { source: PopulateSource; confidence?: number; ts: string; }

export type PartialValues = Record<string, unknown>;

export interface PopulateResult {
  applied: string[];
  skipped: { field: string; reason: 'hidden' | 'unknownField' | 'invalidOption' | 'readOnly' }[];
}

export interface SaveResult {
  ok: boolean;
  recordId?: string;
  errors?: Record<string, string[]>;
}

export interface FormRecord {
  id: string;
  layoutKey: string;
  layoutVersion: number;
  values: PartialValues;
  provenance: Record<string, ProvenanceEntry>;
  context?: unknown;
  createdAt: string;
  updatedAt: string;
}

export interface ExtractResponse {
  values: PartialValues;
  confidences: Record<string, number>;
  clearedFields: string[];
  skipped: { field: string; reason: string }[];
  unmatchedText?: string;
  layer: string;
}

/** Per-field visual state driven by AI population / cascade clears (spec §5). */
export type FieldHighlight = 'ai-confident' | 'ai-review' | 'cascade-cleared' | null;
