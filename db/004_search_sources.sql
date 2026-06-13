-- High-cardinality search sources for typeahead reference fields (spec §4.2 extension).
-- POC seeds representative samples; production binds these keys to the real indexes
-- (the main app's LocalNpiRegistryService over ~9M NPI rows, a drug formulary, etc.).

CREATE TABLE IF NOT EXISTS npi_physician (
  npi        TEXT PRIMARY KEY,
  name       TEXT NOT NULL,
  specialty  TEXT NOT NULL,
  city       TEXT NOT NULL,
  state      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_npi_name ON npi_physician (lower(name));

CREATE TABLE IF NOT EXISTS medication (
  name  TEXT PRIMARY KEY,
  form  TEXT
);
CREATE INDEX IF NOT EXISTS ix_med_name ON medication (lower(name));

-- A handful of named physicians so demo dictation resolves deterministically...
INSERT INTO npi_physician (npi, name, specialty, city, state) VALUES
  ('1003001001','Jane Smith','Anesthesiology','Springfield','IL'),
  ('1003001002','Robert Chen','Gastroenterology','Plano','TX'),
  ('1003001003','Maria Gupta','Anesthesiology','Boston','MA'),
  ('1003001004','David Patel','Cardiology','Houston','TX'),
  ('1003001005','Sarah Johnson','Anesthesiology','Chicago','IL'),
  ('1003001006','Michael Brown','General Surgery','Dallas','TX'),
  ('1003001007','Linda Nguyen','Gastroenterology','San Jose','CA'),
  ('1003001008','James Wilson','Orthopedics','Denver','CO'),
  ('1003001009','Emily Davis','Anesthesiology','Seattle','WA'),
  ('1003001010','Bruce Phillips','Anesthesiology','Plano','TX')
ON CONFLICT (npi) DO NOTHING;

-- ...plus ~400 synthetic rows so the typeahead behaves like a real large dataset.
INSERT INTO npi_physician (npi, name, specialty, city, state)
SELECT
  (1500000000 + g)::text,
  (ARRAY['James','Mary','John','Patricia','Robert','Jennifer','William','Linda','Richard','Elizabeth',
         'Joseph','Susan','Thomas','Jessica','Charles','Karen','Daniel','Nancy','Matthew','Sandra'])[1 + (g % 20)]
    || ' ' ||
  (ARRAY['Smith','Johnson','Williams','Jones','Brown','Garcia','Miller','Davis','Rodriguez','Martinez',
         'Hernandez','Lopez','Gonzalez','Wilson','Anderson','Thomas','Taylor','Moore','Jackson','Martin'])[1 + ((g / 20) % 20)],
  (ARRAY['Anesthesiology','Gastroenterology','Cardiology','General Surgery','Orthopedics','Pulmonology',
         'Nephrology','Internal Medicine','Family Medicine','Pain Management'])[1 + (g % 10)],
  (ARRAY['Springfield','Plano','Boston','Houston','Chicago','Dallas','San Jose','Denver','Seattle','Phoenix',
         'Austin','Columbus','Charlotte','Indianapolis','Portland'])[1 + (g % 15)],
  (ARRAY['IL','TX','MA','TX','IL','TX','CA','CO','WA','AZ','TX','OH','NC','IN','OR'])[1 + (g % 15)]
FROM generate_series(1, 400) g
ON CONFLICT (npi) DO NOTHING;

INSERT INTO medication (name, form) VALUES
  ('Lisinopril','tablet'),('Metformin','tablet'),('Atorvastatin','tablet'),('Amlodipine','tablet'),
  ('Metoprolol','tablet'),('Omeprazole','capsule'),('Losartan','tablet'),('Albuterol','inhaler'),
  ('Gabapentin','capsule'),('Hydrochlorothiazide','tablet'),('Sertraline','tablet'),('Levothyroxine','tablet'),
  ('Simvastatin','tablet'),('Montelukast','tablet'),('Escitalopram','tablet'),('Furosemide','tablet'),
  ('Pantoprazole','tablet'),('Citalopram','tablet'),('Fluoxetine','capsule'),('Tramadol','tablet'),
  ('Clopidogrel','tablet'),('Rosuvastatin','tablet'),('Warfarin','tablet'),('Apixaban','tablet'),
  ('Rivaroxaban','tablet'),('Insulin Glargine','injection'),('Insulin Lispro','injection'),('Aspirin','tablet'),
  ('Acetaminophen','tablet'),('Ibuprofen','tablet'),('Prednisone','tablet'),('Amoxicillin','capsule'),
  ('Azithromycin','tablet'),('Ciprofloxacin','tablet'),('Doxycycline','capsule'),('Cephalexin','capsule'),
  ('Hydrocodone-Acetaminophen','tablet'),('Oxycodone','tablet'),('Morphine','injection'),('Fentanyl','patch'),
  ('Midazolam','injection'),('Propofol','injection'),('Ondansetron','tablet'),('Famotidine','tablet'),
  ('Duloxetine','capsule'),('Venlafaxine','capsule'),('Bupropion','tablet'),('Trazodone','tablet'),
  ('Alprazolam','tablet'),('Lorazepam','tablet'),('Clonazepam','tablet'),('Zolpidem','tablet'),
  ('Tamsulosin','capsule'),('Finasteride','tablet'),('Allopurinol','tablet'),('Spironolactone','tablet'),
  ('Carvedilol','tablet'),('Diltiazem','tablet'),('Digoxin','tablet'),('Enoxaparin','injection'),
  ('Heparin','injection'),('Glipizide','tablet'),('Pioglitazone','tablet'),('Sitagliptin','tablet'),
  ('Empagliflozin','tablet'),('Dulaglutide','injection'),('Budesonide','inhaler'),('Fluticasone','inhaler'),
  ('Tiotropium','inhaler'),('Ipratropium','inhaler'),('Cetirizine','tablet'),('Loratadine','tablet'),
  ('Diphenhydramine','tablet'),('Ranitidine','tablet'),('Sucralfate','tablet'),('Mesalamine','tablet'),
  ('Methotrexate','tablet'),('Hydroxychloroquine','tablet'),('Levetiracetam','tablet'),('Lamotrigine','tablet'),
  ('Topiramate','tablet'),('Pregabalin','capsule'),('Cyclobenzaprine','tablet'),('Baclofen','tablet'),
  ('Tizanidine','tablet'),('Naproxen','tablet'),('Meloxicam','tablet'),('Celecoxib','capsule'),
  ('Nitroglycerin','sublingual'),('Isosorbide Mononitrate','tablet'),('Ranolazine','tablet'),('Hydralazine','tablet')
ON CONFLICT (name) DO NOTHING;
