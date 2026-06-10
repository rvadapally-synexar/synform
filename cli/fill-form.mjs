#!/usr/bin/env node
/**
 * CLI/agent modality (spec §7): no extraction, no UI. The form is a machine endpoint —
 * GET the JSON Schema, build values against it, POST a record.
 *
 *   node cli/fill-form.mjs [layoutKey]
 */
const API = process.env.SYNFORM_API ?? 'http://localhost:5266';
const layoutKey = process.argv[2] ?? 'pre-anesthesia-assessment';

// 1. Fetch the machine surface.
const schemaRes = await fetch(`${API}/api/layouts/${layoutKey}/schema`);
if (!schemaRes.ok) { console.error(`No schema for '${layoutKey}' (${schemaRes.status})`); process.exit(1); }
const schema = await schemaRes.json();
const version = Number(schema.$id.split(':v').pop());

console.log(`Schema: ${schema.title} (${schema.$id})`);
console.log(`Required: ${schema.required.join(', ')}\n`);

// 2. Fill values an agent might produce (synthetic test data only).
const values = {
  patientName: 'CLI Agent Test',
  dob: '1975-03-22',
  heightCm: 168,
  weightKg: 74,
  asaClass: '2',
  mallampati: '2',
  bp: { sys: 118, dia: 76 },
  heartRate: 68,
  allergies: ['nkda'],
  npoTime: '01:30',
  comorbidities: ['htn', 'gerd'],
  smoker: 'no',
  procedureCategory: 'gi',
  procedure: 'egd',
  procedureTime: '08:45',
  consentVerified: true,
};

// Drop anything the schema doesn't know (defensive: layouts evolve).
for (const k of Object.keys(values)) if (!schema.properties[k]) delete values[k];

// 3. Save through the same validated endpoint as every other modality.
const save = await fetch(`${API}/api/records`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    layoutKey,
    layoutVersion: version,
    values,
    provenance: Object.fromEntries(Object.keys(values).map(k =>
      [k, { source: 'agent', confidence: 1, ts: new Date().toISOString() }])),
  }),
});

const body = await save.json();
if (!save.ok) {
  console.error(`Save rejected (${save.status}):`, JSON.stringify(body.errors, null, 2));
  process.exit(1);
}
console.log(`Saved record: ${body.recordId} (layout v${version})`);
