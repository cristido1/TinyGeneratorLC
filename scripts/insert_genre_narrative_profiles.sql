BEGIN TRANSACTION;

-- ============================================================
-- Nuovi Narrative Profile orientati al GENERE (non alla storia)
-- Inserimento idempotente: non duplica se il nome esiste gia.
-- ============================================================

-- 1) Fantasy Epica
INSERT INTO narrative_profiles (name, description, base_system_prompt, style_prompt, pov_list_json)
SELECT
    'Fantasy Epica',
    'Fantasy | Epic Adventure | Medievale immaginario | Tono: Solenne e avventuroso',
    'Sei uno storyteller di fantasy epico. Mantieni coerenza del mondo, senso del meraviglioso e conflitti morali.',
    'Prosa evocativa ma chiara. Ritmo alternato tra tensione, scoperta e scelta etica. Evita riferimenti moderni.',
    '["ThirdPersonLimited","FirstPerson"]'
WHERE NOT EXISTS (SELECT 1 FROM narrative_profiles WHERE name = 'Fantasy Epica');

INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'Magia', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Fantasy Epica'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'Magia');
INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'Coesione', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Fantasy Epica'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'Coesione');
INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'Morale', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Fantasy Epica'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'Morale');

INSERT INTO micro_objectives (narrative_profile_id, code, description, difficulty)
SELECT p.id, 'UNITE_ALLIES', 'Unire alleati con interessi in conflitto', 2 FROM narrative_profiles p
WHERE p.name = 'Fantasy Epica'
  AND NOT EXISTS (SELECT 1 FROM micro_objectives m WHERE m.narrative_profile_id = p.id AND m.code = 'UNITE_ALLIES');
INSERT INTO micro_objectives (narrative_profile_id, code, description, difficulty)
SELECT p.id, 'PROTECT_RELIC', 'Proteggere un artefatto o sapere cruciale', 3 FROM narrative_profiles p
WHERE p.name = 'Fantasy Epica'
  AND NOT EXISTS (SELECT 1 FROM micro_objectives m WHERE m.narrative_profile_id = p.id AND m.code = 'PROTECT_RELIC');

INSERT INTO failure_rules (narrative_profile_id, description, trigger_type)
SELECT p.id, 'Orgoglio o impulsivita causano una scelta tattica sbagliata', 'RandomUnderPressure' FROM narrative_profiles p
WHERE p.name = 'Fantasy Epica'
  AND NOT EXISTS (SELECT 1 FROM failure_rules f WHERE f.narrative_profile_id = p.id AND f.trigger_type = 'RandomUnderPressure');
INSERT INTO failure_rules (narrative_profile_id, description, trigger_type)
SELECT p.id, 'Esaurimento di risorse arcane o logistiche', 'ResourceBelowThreshold' FROM narrative_profiles p
WHERE p.name = 'Fantasy Epica'
  AND NOT EXISTS (SELECT 1 FROM failure_rules f WHERE f.narrative_profile_id = p.id AND f.trigger_type = 'ResourceBelowThreshold');

INSERT INTO consequence_rules (narrative_profile_id, description)
SELECT p.id, 'Rottura del fronte e dispersione degli alleati' FROM narrative_profiles p
WHERE p.name = 'Fantasy Epica'
  AND NOT EXISTS (SELECT 1 FROM consequence_rules c WHERE c.narrative_profile_id = p.id AND c.description = 'Rottura del fronte e dispersione degli alleati');
INSERT INTO consequence_rules (narrative_profile_id, description)
SELECT p.id, 'Costo magico e psicologico della vittoria parziale' FROM narrative_profiles p
WHERE p.name = 'Fantasy Epica'
  AND NOT EXISTS (SELECT 1 FROM consequence_rules c WHERE c.narrative_profile_id = p.id AND c.description = 'Costo magico e psicologico della vittoria parziale');

INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Coesione', -14 FROM consequence_rules c
JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Fantasy Epica' AND c.description = 'Rottura del fronte e dispersione degli alleati'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Coesione');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Morale', -12 FROM consequence_rules c
JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Fantasy Epica' AND c.description = 'Rottura del fronte e dispersione degli alleati'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Morale');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Magia', -16 FROM consequence_rules c
JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Fantasy Epica' AND c.description = 'Costo magico e psicologico della vittoria parziale'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Magia');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Morale', -10 FROM consequence_rules c
JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Fantasy Epica' AND c.description = 'Costo magico e psicologico della vittoria parziale'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Morale');

-- 2) Giallo Investigativo
INSERT INTO narrative_profiles (name, description, base_system_prompt, style_prompt, pov_list_json)
SELECT
    'Giallo Investigativo',
    'Crime | Mystery | Contemporaneo | Tono: Teso e razionale',
    'Sei uno storyteller di giallo investigativo. Dai priorita a logica, indizi, conflitti tra versioni dei fatti.',
    'Stile concreto e preciso. Ogni scena deve aggiungere un indizio, un sospetto o un ostacolo alla verita.',
    '["ThirdPersonLimited"]'
WHERE NOT EXISTS (SELECT 1 FROM narrative_profiles WHERE name = 'Giallo Investigativo');

INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'Indizi', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Giallo Investigativo'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'Indizi');
INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'Credibilita', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Giallo Investigativo'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'Credibilita');
INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'Tempo', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Giallo Investigativo'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'Tempo');

INSERT INTO micro_objectives (narrative_profile_id, code, description, difficulty)
SELECT p.id, 'VERIFY_ALIBI', 'Verificare alibi contraddittori', 2 FROM narrative_profiles p
WHERE p.name = 'Giallo Investigativo'
  AND NOT EXISTS (SELECT 1 FROM micro_objectives m WHERE m.narrative_profile_id = p.id AND m.code = 'VERIFY_ALIBI');
INSERT INTO micro_objectives (narrative_profile_id, code, description, difficulty)
SELECT p.id, 'FIND_MOTIVE', 'Identificare movente e opportunita reali', 3 FROM narrative_profiles p
WHERE p.name = 'Giallo Investigativo'
  AND NOT EXISTS (SELECT 1 FROM micro_objectives m WHERE m.narrative_profile_id = p.id AND m.code = 'FIND_MOTIVE');

INSERT INTO failure_rules (narrative_profile_id, description, trigger_type)
SELECT p.id, 'Deduzione affrettata su un indizio ambiguo', 'RandomUnderPressure' FROM narrative_profiles p
WHERE p.name = 'Giallo Investigativo'
  AND NOT EXISTS (SELECT 1 FROM failure_rules f WHERE f.narrative_profile_id = p.id AND f.trigger_type = 'RandomUnderPressure');
INSERT INTO failure_rules (narrative_profile_id, description, trigger_type)
SELECT p.id, 'Tempo e credibilita investigativa in calo', 'ResourceBelowThreshold' FROM narrative_profiles p
WHERE p.name = 'Giallo Investigativo'
  AND NOT EXISTS (SELECT 1 FROM failure_rules f WHERE f.narrative_profile_id = p.id AND f.trigger_type = 'ResourceBelowThreshold');

INSERT INTO consequence_rules (narrative_profile_id, description)
SELECT p.id, 'Pista principale compromessa' FROM narrative_profiles p
WHERE p.name = 'Giallo Investigativo'
  AND NOT EXISTS (SELECT 1 FROM consequence_rules c WHERE c.narrative_profile_id = p.id AND c.description = 'Pista principale compromessa');
INSERT INTO consequence_rules (narrative_profile_id, description)
SELECT p.id, 'Sospettati allertati e collaborazione ridotta' FROM narrative_profiles p
WHERE p.name = 'Giallo Investigativo'
  AND NOT EXISTS (SELECT 1 FROM consequence_rules c WHERE c.narrative_profile_id = p.id AND c.description = 'Sospettati allertati e collaborazione ridotta');

INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Indizi', -15 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Giallo Investigativo' AND c.description = 'Pista principale compromessa'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Indizi');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Tempo', -12 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Giallo Investigativo' AND c.description = 'Pista principale compromessa'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Tempo');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Credibilita', -14 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Giallo Investigativo' AND c.description = 'Sospettati allertati e collaborazione ridotta'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Credibilita');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Tempo', -9 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Giallo Investigativo' AND c.description = 'Sospettati allertati e collaborazione ridotta'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Tempo');

-- 3) Horror Psicologico
INSERT INTO narrative_profiles (name, description, base_system_prompt, style_prompt, pov_list_json)
SELECT
    'Horror Psicologico',
    'Horror | Psicologico | Contemporaneo o isolato | Tono: Inquietante e claustrofobico',
    'Sei uno storyteller di horror psicologico. Coltiva ambiguita percettiva, paura crescente e vulnerabilita umana.',
    'Frasi nitide, immagini sensoriali precise, escalation lenta ma costante. Evita gore gratuito senza funzione narrativa.',
    '["FirstPerson","ThirdPersonLimited"]'
WHERE NOT EXISTS (SELECT 1 FROM narrative_profiles WHERE name = 'Horror Psicologico');

INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'SanitaMentale', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Horror Psicologico'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'SanitaMentale');
INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'Sicurezza', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Horror Psicologico'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'Sicurezza');
INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'Fiducia', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Horror Psicologico'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'Fiducia');

INSERT INTO micro_objectives (narrative_profile_id, code, description, difficulty)
SELECT p.id, 'VERIFY_REALITY', 'Distinguere tra minaccia reale e distorsione', 3 FROM narrative_profiles p
WHERE p.name = 'Horror Psicologico'
  AND NOT EXISTS (SELECT 1 FROM micro_objectives m WHERE m.narrative_profile_id = p.id AND m.code = 'VERIFY_REALITY');
INSERT INTO micro_objectives (narrative_profile_id, code, description, difficulty)
SELECT p.id, 'MAINTAIN_GROUP', 'Mantenere coesione in condizioni di panico', 2 FROM narrative_profiles p
WHERE p.name = 'Horror Psicologico'
  AND NOT EXISTS (SELECT 1 FROM micro_objectives m WHERE m.narrative_profile_id = p.id AND m.code = 'MAINTAIN_GROUP');

INSERT INTO failure_rules (narrative_profile_id, description, trigger_type)
SELECT p.id, 'Panico o paranoia alterano il giudizio', 'RandomUnderPressure' FROM narrative_profiles p
WHERE p.name = 'Horror Psicologico'
  AND NOT EXISTS (SELECT 1 FROM failure_rules f WHERE f.narrative_profile_id = p.id AND f.trigger_type = 'RandomUnderPressure');
INSERT INTO failure_rules (narrative_profile_id, description, trigger_type)
SELECT p.id, 'Calo di sicurezza e lucidita oltre soglia', 'ResourceBelowThreshold' FROM narrative_profiles p
WHERE p.name = 'Horror Psicologico'
  AND NOT EXISTS (SELECT 1 FROM failure_rules f WHERE f.narrative_profile_id = p.id AND f.trigger_type = 'ResourceBelowThreshold');

INSERT INTO consequence_rules (narrative_profile_id, description)
SELECT p.id, 'Separazione del gruppo in area ostile' FROM narrative_profiles p
WHERE p.name = 'Horror Psicologico'
  AND NOT EXISTS (SELECT 1 FROM consequence_rules c WHERE c.narrative_profile_id = p.id AND c.description = 'Separazione del gruppo in area ostile');
INSERT INTO consequence_rules (narrative_profile_id, description)
SELECT p.id, 'Crollo di fiducia reciproca' FROM narrative_profiles p
WHERE p.name = 'Horror Psicologico'
  AND NOT EXISTS (SELECT 1 FROM consequence_rules c WHERE c.narrative_profile_id = p.id AND c.description = 'Crollo di fiducia reciproca');

INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Sicurezza', -16 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Horror Psicologico' AND c.description = 'Separazione del gruppo in area ostile'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Sicurezza');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'SanitaMentale', -13 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Horror Psicologico' AND c.description = 'Separazione del gruppo in area ostile'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'SanitaMentale');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Fiducia', -15 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Horror Psicologico' AND c.description = 'Crollo di fiducia reciproca'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Fiducia');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'SanitaMentale', -10 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Horror Psicologico' AND c.description = 'Crollo di fiducia reciproca'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'SanitaMentale');

-- 4) Thriller Tecnologico
INSERT INTO narrative_profiles (name, description, base_system_prompt, style_prompt, pov_list_json)
SELECT
    'Thriller Tecnologico',
    'Thriller | Techno-thriller | Contemporaneo o near-future | Tono: Urgente e preciso',
    'Sei uno storyteller di thriller tecnologico. Bilancia tensione operativa, rischio sistemico e decisioni sotto deadline.',
    'Stile asciutto e cinematico. Mantieni chiarezza su obiettivi, vincoli tecnici e posta in gioco.',
    '["ThirdPersonLimited"]'
WHERE NOT EXISTS (SELECT 1 FROM narrative_profiles WHERE name = 'Thriller Tecnologico');

INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'Accesso', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Thriller Tecnologico'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'Accesso');
INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'Copertura', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Thriller Tecnologico'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'Copertura');
INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'Tempo', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Thriller Tecnologico'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'Tempo');

INSERT INTO micro_objectives (narrative_profile_id, code, description, difficulty)
SELECT p.id, 'BREACH_NODE', 'Accedere a un nodo critico senza esposizione', 3 FROM narrative_profiles p
WHERE p.name = 'Thriller Tecnologico'
  AND NOT EXISTS (SELECT 1 FROM micro_objectives m WHERE m.narrative_profile_id = p.id AND m.code = 'BREACH_NODE');
INSERT INTO micro_objectives (narrative_profile_id, code, description, difficulty)
SELECT p.id, 'ISOLATE_THREAT', 'Isolare una minaccia prima dell''escalation', 2 FROM narrative_profiles p
WHERE p.name = 'Thriller Tecnologico'
  AND NOT EXISTS (SELECT 1 FROM micro_objectives m WHERE m.narrative_profile_id = p.id AND m.code = 'ISOLATE_THREAT');

INSERT INTO failure_rules (narrative_profile_id, description, trigger_type)
SELECT p.id, 'Patch o azione rapida introduce nuova vulnerabilita', 'RandomUnderPressure' FROM narrative_profiles p
WHERE p.name = 'Thriller Tecnologico'
  AND NOT EXISTS (SELECT 1 FROM failure_rules f WHERE f.narrative_profile_id = p.id AND f.trigger_type = 'RandomUnderPressure');
INSERT INTO failure_rules (narrative_profile_id, description, trigger_type)
SELECT p.id, 'Riduzione di accesso, copertura o tempo operativo', 'ResourceBelowThreshold' FROM narrative_profiles p
WHERE p.name = 'Thriller Tecnologico'
  AND NOT EXISTS (SELECT 1 FROM failure_rules f WHERE f.narrative_profile_id = p.id AND f.trigger_type = 'ResourceBelowThreshold');

INSERT INTO consequence_rules (narrative_profile_id, description)
SELECT p.id, 'Tracciamento dell''operazione da parte dell''avversario' FROM narrative_profiles p
WHERE p.name = 'Thriller Tecnologico'
  AND NOT EXISTS (SELECT 1 FROM consequence_rules c WHERE c.narrative_profile_id = p.id AND c.description = 'Tracciamento dell''operazione da parte dell''avversario');
INSERT INTO consequence_rules (narrative_profile_id, description)
SELECT p.id, 'Timeout operativo e perdita di finestre utili' FROM narrative_profiles p
WHERE p.name = 'Thriller Tecnologico'
  AND NOT EXISTS (SELECT 1 FROM consequence_rules c WHERE c.narrative_profile_id = p.id AND c.description = 'Timeout operativo e perdita di finestre utili');

INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Copertura', -16 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Thriller Tecnologico' AND c.description = 'Tracciamento dell''operazione da parte dell''avversario'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Copertura');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Accesso', -12 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Thriller Tecnologico' AND c.description = 'Tracciamento dell''operazione da parte dell''avversario'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Accesso');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Tempo', -18 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Thriller Tecnologico' AND c.description = 'Timeout operativo e perdita di finestre utili'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Tempo');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Accesso', -8 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Thriller Tecnologico' AND c.description = 'Timeout operativo e perdita di finestre utili'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Accesso');

-- 5) Romance Drammatico
INSERT INTO narrative_profiles (name, description, base_system_prompt, style_prompt, pov_list_json)
SELECT
    'Romance Drammatico',
    'Romance | Drama | Contemporaneo | Tono: Emotivo e realistico',
    'Sei uno storyteller di romance drammatico. Dai centralita a legami, vulnerabilita, conflitto interiore e scelta relazionale.',
    'Dialoghi naturali, sottotesto emotivo, conseguenze concrete. Evita idealizzazione piatta.',
    '["FirstPerson","ThirdPersonLimited"]'
WHERE NOT EXISTS (SELECT 1 FROM narrative_profiles WHERE name = 'Romance Drammatico');

INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'Fiducia', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Romance Drammatico'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'Fiducia');
INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'Comunicazione', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Romance Drammatico'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'Comunicazione');
INSERT INTO narrative_resources (narrative_profile_id, name, initial_value, min_value, max_value)
SELECT p.id, 'StabilitaEmotiva', 100, 0, 100 FROM narrative_profiles p
WHERE p.name = 'Romance Drammatico'
  AND NOT EXISTS (SELECT 1 FROM narrative_resources r WHERE r.narrative_profile_id = p.id AND r.name = 'StabilitaEmotiva');

INSERT INTO micro_objectives (narrative_profile_id, code, description, difficulty)
SELECT p.id, 'CLARIFY_BOUNDARIES', 'Definire confini e aspettative reciproche', 2 FROM narrative_profiles p
WHERE p.name = 'Romance Drammatico'
  AND NOT EXISTS (SELECT 1 FROM micro_objectives m WHERE m.narrative_profile_id = p.id AND m.code = 'CLARIFY_BOUNDARIES');
INSERT INTO micro_objectives (narrative_profile_id, code, description, difficulty)
SELECT p.id, 'REPAIR_BREACH', 'Riparare una frattura relazionale significativa', 3 FROM narrative_profiles p
WHERE p.name = 'Romance Drammatico'
  AND NOT EXISTS (SELECT 1 FROM micro_objectives m WHERE m.narrative_profile_id = p.id AND m.code = 'REPAIR_BREACH');

INSERT INTO failure_rules (narrative_profile_id, description, trigger_type)
SELECT p.id, 'Reazione impulsiva che chiude il dialogo', 'RandomUnderPressure' FROM narrative_profiles p
WHERE p.name = 'Romance Drammatico'
  AND NOT EXISTS (SELECT 1 FROM failure_rules f WHERE f.narrative_profile_id = p.id AND f.trigger_type = 'RandomUnderPressure');
INSERT INTO failure_rules (narrative_profile_id, description, trigger_type)
SELECT p.id, 'Calo di fiducia e stabilita emotiva sotto soglia', 'ResourceBelowThreshold' FROM narrative_profiles p
WHERE p.name = 'Romance Drammatico'
  AND NOT EXISTS (SELECT 1 FROM failure_rules f WHERE f.narrative_profile_id = p.id AND f.trigger_type = 'ResourceBelowThreshold');

INSERT INTO consequence_rules (narrative_profile_id, description)
SELECT p.id, 'Silenzi prolungati e incomprensioni cumulative' FROM narrative_profiles p
WHERE p.name = 'Romance Drammatico'
  AND NOT EXISTS (SELECT 1 FROM consequence_rules c WHERE c.narrative_profile_id = p.id AND c.description = 'Silenzi prolungati e incomprensioni cumulative');
INSERT INTO consequence_rules (narrative_profile_id, description)
SELECT p.id, 'Rottura temporanea della relazione' FROM narrative_profiles p
WHERE p.name = 'Romance Drammatico'
  AND NOT EXISTS (SELECT 1 FROM consequence_rules c WHERE c.narrative_profile_id = p.id AND c.description = 'Rottura temporanea della relazione');

INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Comunicazione', -16 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Romance Drammatico' AND c.description = 'Silenzi prolungati e incomprensioni cumulative'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Comunicazione');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Fiducia', -12 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Romance Drammatico' AND c.description = 'Silenzi prolungati e incomprensioni cumulative'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Fiducia');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'Fiducia', -18 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Romance Drammatico' AND c.description = 'Rottura temporanea della relazione'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'Fiducia');
INSERT INTO consequence_impacts (consequence_rule_id, resource_name, delta_value)
SELECT c.id, 'StabilitaEmotiva', -14 FROM consequence_rules c JOIN narrative_profiles p ON p.id = c.narrative_profile_id
WHERE p.name = 'Romance Drammatico' AND c.description = 'Rottura temporanea della relazione'
  AND NOT EXISTS (SELECT 1 FROM consequence_impacts i WHERE i.consequence_rule_id = c.id AND i.resource_name = 'StabilitaEmotiva');

COMMIT;
