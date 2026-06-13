-- Publish a new version of the pre-anesthesia layout that exercises the three
-- high-cardinality field patterns: physician (NPI single search), medications
-- (multi search), comorbidities (free-text tags, dataType unchanged = string[]).
-- Idempotent: only inserts if no published version already has the 'medications' field.

INSERT INTO form_layout (layout_key, version, status, json, published_at)
SELECT 'pre-anesthesia-assessment',
       (SELECT COALESCE(MAX(version), 0) + 1 FROM form_layout WHERE layout_key = 'pre-anesthesia-assessment'),
       'published', $j$
{
  "title": "Pre-Anesthesia Assessment",
  "fields": [
    {"name":"patientName","label":"Patient Name","controlType":"text","dataType":"string","required":true,"group":"patient","order":10,
     "aliases":["patient","name","patient name"]},
    {"name":"dob","label":"Date of Birth","controlType":"date","dataType":"date","required":true,"max":"today","group":"patient","order":20,
     "aliases":["DOB","date of birth","born"]},
    {"name":"attendingPhysician","label":"Attending Physician","controlType":"search","dataType":"string","group":"patient","order":25,
     "options":{"searchKey":"physician"},
     "aliases":["physician","attending","attending physician","provider","doctor","surgeon"]},
    {"name":"heightCm","label":"Height","controlType":"number","dataType":"number","unit":"cm","min":30,"max":250,"group":"patient","order":30,
     "aliases":["height"]},
    {"name":"weightKg","label":"Weight","controlType":"number","dataType":"number","unit":"kg","min":1,"max":400,"group":"patient","order":40,
     "aliases":["weight"]},
    {"name":"bmi","label":"BMI","controlType":"number","dataType":"number","group":"patient","order":50,
     "computedFrom":{"fn":"bmi","inputs":["heightCm","weightKg"]}},
    {"name":"asaClass","label":"ASA Classification","controlType":"radio","dataType":"string","required":true,"group":"assessment","order":10,
     "options":{"inline":[{"value":"1","label":"ASA I"},{"value":"2","label":"ASA II"},{"value":"3","label":"ASA III"},{"value":"4","label":"ASA IV"}]},
     "aliases":["ASA","ASA class","physical status"]},
    {"name":"mallampati","label":"Mallampati Score","controlType":"dropdown","dataType":"string","group":"airway","order":10,
     "options":{"inline":[{"value":"1","label":"Class I"},{"value":"2","label":"Class II"},{"value":"3","label":"Class III"},{"value":"4","label":"Class IV"}]},
     "aliases":["mallampati","mallampati class","mallampati score"]},
    {"name":"bp","label":"Blood Pressure","controlType":"bpPair","dataType":"bpPair","group":"assessment","order":20,
     "bpBounds":{"sysMin":60,"sysMax":260,"diaMin":30,"diaMax":160},
     "aliases":["blood pressure","BP","pressure"]},
    {"name":"heartRate","label":"Heart Rate","controlType":"number","dataType":"number","unit":"bpm","min":20,"max":250,"group":"assessment","order":30,
     "aliases":["heart rate","pulse","HR"]},
    {"name":"allergies","label":"Allergies","controlType":"multiselect","dataType":"string[]","group":"assessment","order":40,
     "options":{"lookupKey":"allergies"},
     "aliases":["allergy","allergies","allergic to"]},
    {"name":"allergyOther","label":"Other Allergy (specify)","controlType":"text","dataType":"string","group":"assessment","order":50,
     "visibleWhen":{"field":"allergies","op":"contains","value":"other"},
     "aliases":["other allergy"]},
    {"name":"medications","label":"Current Medications","controlType":"searchMulti","dataType":"string[]","group":"assessment","order":55,
     "options":{"searchKey":"medication"},
     "aliases":["medications","current medications","meds","taking","home medications"]},
    {"name":"npoTime","label":"NPO Since","controlType":"time","dataType":"time","required":true,"group":"assessment","order":60,
     "aliases":["NPO","NPO time","nothing by mouth","fasting since"]},
    {"name":"comorbidities","label":"Comorbidities","controlType":"tags","dataType":"string[]","group":"assessment","order":70,
     "options":{"searchKey":"comorbidity"},
     "aliases":["comorbidities","medical history","conditions","past medical history"]},
    {"name":"smoker","label":"Smoking Status","controlType":"radio","dataType":"string","group":"assessment","order":80,
     "options":{"inline":[{"value":"yes","label":"Yes"},{"value":"no","label":"No"},{"value":"former","label":"Former"}]},
     "aliases":["smoker","smoking","smoking status","tobacco"]},
    {"name":"packYears","label":"Pack Years","controlType":"number","dataType":"number","min":0,"max":200,"group":"assessment","order":90,
     "visibleWhen":{"field":"smoker","op":"in","value":["yes","former"]},
     "requiredWhen":{"field":"smoker","op":"eq","value":"yes"},
     "aliases":["pack years"]},
    {"name":"procedureCategory","label":"Procedure Category","controlType":"dropdown","dataType":"string","required":true,"group":"procedure","order":10,
     "options":{"lookupKey":"procedureCategories"},
     "aliases":["category","procedure category","specialty"]},
    {"name":"procedure","label":"Procedure","controlType":"dropdown","dataType":"string","required":true,"group":"procedure","order":20,
     "options":{"lookupKey":"procedures"},"filterBy":"procedureCategory",
     "aliases":["procedure","operation","surgery"]},
    {"name":"procedureTime","label":"Procedure Time","controlType":"time","dataType":"time","required":true,"group":"procedure","order":30,
     "aliases":["procedure time","surgery time","scheduled for"]},
    {"name":"airwayNotes","label":"Airway Notes","controlType":"textarea","dataType":"string","group":"airway","order":20,
     "aliases":["airway","airway notes"]},
    {"name":"consentVerified","label":"Consent Verified","controlType":"checkbox","dataType":"boolean","mustBeTrue":true,"group":"procedure","order":40,
     "aliases":["consent","consent verified"]}
  ],
  "crossFieldRules": [
    {"id":"npo-before-procedure",
     "message":"NPO time must be before procedure time",
     "condition":{"field":"npoTime","op":"lt","valueFromField":"procedureTime"}}
  ]
}
$j$::jsonb, now()
WHERE NOT EXISTS (
  SELECT 1 FROM form_layout
  WHERE layout_key = 'pre-anesthesia-assessment'
    AND json->'fields' @> '[{"name":"medications"}]'::jsonb
);
