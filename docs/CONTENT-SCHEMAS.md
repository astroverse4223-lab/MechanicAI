# Built-in Reference Content Schemas

Mechanic AI ships with curated reference content as embedded JSON under
`src/MechanicAI.Infrastructure/Content/`. It is loaded at startup, validated, and
seeded into the local database. This document is the contract between the
content files and the code that consumes them.

## Ground rules for all content

1. **Never invent vehicle-specific specifications.** No torque values, resistance
   values, pressures, capacities, pinouts, or connector numbers for specific
   vehicles. When a value is needed, write "per OEM service information" or
   "refer to the manufacturer specification for this vehicle".
2. **General guidelines must be labeled.** A widely used generic rule of thumb
   (for example "total fuel trim beyond roughly ±10% generally warrants
   investigation") must be written as a general guideline, e.g. suffix
   "(general guideline — verify against OEM specification)".
3. **Test before replace.** Procedures verify a fault before recommending parts.
4. **Safety first.** Tag content with the safety vocabulary below whenever it applies.
5. **Plain, professional technician language.** US English. No marketing tone.
6. **Manufacturer-specific codes** (P1xxx, B1xxx, C1xxx, U1xxx, etc.) are NOT given
   descriptions in the generic reference, because meanings differ by OEM.

### Safety tag vocabulary

| tag | meaning |
|-----|---------|
| `airbag` | SRS / airbags / pretensioners / clockspring |
| `fuel` | pressurized fuel, fuel vapors, fire risk |
| `high-voltage` | hybrid / EV high-voltage systems (orange cables, HV battery) |
| `lifting` | vehicle lifting, jack stands, wheels off the ground |
| `rotating` | running engine, belts, fans, driveline |
| `compressed` | springs, struts, pressurized systems (except refrigerant/fuel) |
| `high-current` | battery, starter, alternator circuits; short-circuit burns |
| `brakes` | brake hydraulics, brake function affecting vehicle safety |
| `refrigerant` | A/C refrigerant handling (R-134a, R-1234yf), EPA 609 |
| `hot-surfaces` | hot coolant, exhaust, turbochargers, pressurized cooling system |
| `ignition-voltage` | secondary ignition voltage |
| `exhaust-gas` | running engines indoors, carbon monoxide |

### Diagnostic categories (`category` fields)

`Ignition, Fuel, AirIntake, Vacuum, Mechanical, Electrical, Sensor, Emissions,
Exhaust, Cooling, Lubrication, Transmission, Drivetrain, Network, Module, Software,
Charging, Starting, Brakes, Suspension, Steering, Hvac, Body, Restraints,
HighVoltage, Other`

---

## 1. DTC reference — `Content/Dtc/*.json`

```json
{
  "version": "2026.09.1",
  "source": "SAE J2012 / ISO 15031-6 generic DTC definitions (built-in reference)",
  "codes": [
    {
      "code": "P0302",
      "description": "Cylinder 2 Misfire Detected",
      "subsystem": "Ignition System or Misfire",
      "symptoms": ["Rough idle", "Engine shake", "Flashing MIL under load"],
      "causes": ["Spark plug", "Ignition coil", "Fuel injector", "Low compression", "Vacuum leak near cylinder 2"],
      "notes": "General diagnostic notes (optional).",
      "related": ["P0300", "P0352"],
      "safety": ["ignition-voltage", "rotating"]
    }
  ]
}
```

* `code`, `description`, `subsystem` are required. Everything else is optional.
* `symptoms` / `causes` are *common, general* possibilities — the app labels them
  "Unconfirmed possibility". Keep them short (2–8 words each).
* Only include codes whose generic definition you are confident about.

---

## 2. Diagnostic playbooks — `Content/Playbooks/*.json`

A playbook turns a DTC family or symptom pattern into a probabilistic diagnostic
tree. The engine merges every playbook whose triggers match a session, then uses
Bayesian updating and expected-information-gain to recommend the next test.

```json
{
  "version": "2026.09.1",
  "playbooks": [
    {
      "key": "misfire-single-cylinder",
      "title": "Single-cylinder misfire",
      "summary": "One-paragraph overview of the diagnostic strategy.",
      "triggers": {
        "dtcPatterns": ["^P030[1-9]$", "^P031[0-2]$"],
        "symptomKeywords": ["misfire", "shakes", "rough idle", "stumble"]
      },
      "clarifyingQuestions": [
        "Does the misfire occur cold, hot, or both?",
        "Any recent ignition, fuel, or engine work?"
      ],
      "causes": [
        {
          "key": "ignition-coil",
          "title": "Failed ignition coil",
          "category": "Ignition",
          "prior": 0.25,
          "description": "Why this cause produces the complaint.",
          "modifiers": [
            { "when": "dtc", "match": "^P035[1-9]$", "factor": 3.0, "reason": "Coil circuit code present" },
            { "when": "symptom", "match": "rain|wet|humid", "factor": 1.4, "reason": "Moisture-related coil tracking" },
            { "when": "mileage-over", "value": 100000, "factor": 1.2, "reason": "Age-related wear" }
          ],
          "safety": ["ignition-voltage"]
        }
      ],
      "tests": [
        {
          "key": "misfire-coil-swap",
          "title": "Swap the ignition coil to another cylinder",
          "purpose": "Determine whether the misfire follows the coil.",
          "procedure": ["Step 1 ...", "Step 2 ..."],
          "tools": ["Scan tool with misfire counters", "Basic hand tools"],
          "expected": "Describe the normal/passing observation qualitatively or 'per OEM specification'.",
          "specNote": "Optional: which OEM specification the technician must look up.",
          "minutes": 20,
          "difficulty": 1,
          "invasiveness": 1,
          "safety": ["rotating", "ignition-voltage"],
          "requiresAnyCause": ["ignition-coil", "spark-plug"],
          "outcomes": [
            {
              "key": "moved",
              "label": "Misfire moved with the coil",
              "normal": false,
              "likelihoods": { "ignition-coil": 0.95, "*": 0.05 },
              "interpretation": "The coil is strongly implicated."
            },
            {
              "key": "stayed",
              "label": "Misfire stayed on the original cylinder",
              "normal": true,
              "likelihoods": { "ignition-coil": 0.05, "*": 0.95 },
              "interpretation": "Coil is unlikely; continue with plug, injector, mechanical."
            },
            {
              "key": "inconclusive",
              "label": "Could not reproduce the misfire",
              "inconclusive": true,
              "interpretation": "No change in probabilities; try under the conditions from freeze frame."
            }
          ]
        }
      ],
      "verification": [
        "Clear DTCs and record freeze frame first.",
        "Operate the vehicle under the conditions that set the code.",
        "Confirm misfire counters remain at zero and no DTC returns."
      ]
    }
  ]
}
```

### Semantics

* `prior` — relative prior probability of the cause for this pattern (the engine
  normalizes; values need not sum to 1).
* `modifiers[].when` — `dtc` (regex on each DTC), `symptom` (case-insensitive regex
  on complaint/symptom text), `mileage-over` (numeric `value`). `factor`
  multiplies the prior.
* `outcomes[].likelihoods` — **P(outcome | cause)** for each cause key. `"*"` is the
  default for causes not listed. The engine normalizes per cause across the
  non-inconclusive outcomes. A cause missing from the map and no `"*"` means the
  test does not discriminate that cause (uniform).
* `normal: true` → the outcome is shown as **PASS** (within expected);
  `normal: false` → **FAIL** (abnormal finding).
* `inconclusive: true` → recorded, but does not change probabilities.
* `requiresAnyCause` — the test is only offered if at least one listed cause is in the tree.
* `difficulty` / `invasiveness` — 1 (easy, non-invasive) to 5 (major disassembly).
* Cause and test keys are **global**: the same key in two playbooks refers to the
  same thing and is merged (see the canonical key list below). Use lowercase kebab-case.

### Canonical cause keys (reuse these when applicable)

```
spark-plug, ignition-coil, ignition-coil-circuit, fuel-injector, injector-circuit,
low-fuel-pressure, fuel-pump, fuel-filter, fuel-pressure-regulator, contaminated-fuel,
high-pressure-fuel-pump, fuel-rail-pressure-sensor, vacuum-leak, intake-manifold-gasket,
pcv-system-leak, unmetered-air-leak, maf-contaminated, maf-sensor-failed, maf-circuit,
map-sensor, iat-sensor, ect-sensor, thermostat-stuck-open, exhaust-leak-upstream,
o2-sensor-failed, af-sensor-failed, o2-heater-circuit, catalytic-converter-degraded,
low-compression, burnt-valve, head-gasket, valve-train-wear, timing-chain-stretch,
timing-jumped, crank-sensor, cam-sensor, reluctor-damage, vvt-solenoid, vvt-phaser,
low-oil-level, wrong-oil-viscosity, throttle-body-dirty, etc-throttle-body,
pedal-position-sensor, evap-gas-cap, evap-purge-valve, evap-vent-valve,
evap-hose-leak, evap-canister, egr-valve, egr-passages-clogged, knock-sensor,
knock-sensor-circuit, wiring-harness-damage, connector-corrosion, poor-ground,
low-system-voltage, battery-failed, alternator-failed, charging-circuit,
starter-failed, starter-circuit, parasitic-draw, can-bus-wiring, module-power-ground,
module-failed, pcm-software, pcm-failure, wheel-speed-sensor, tone-ring, abs-module,
brake-pad-wear, rotor-runout, caliper-sticking, low-refrigerant, ac-compressor,
ac-clutch-circuit, blend-door-actuator, radiator-restricted, water-pump, cooling-fan,
coolant-leak, air-in-cooling-system, boost-leak, wastegate, turbocharger-failed,
transmission-fluid-low, shift-solenoid, tcc-solenoid, valve-body,
internal-transmission-wear, tcm-failure, speed-sensor
```

---

## 3. Training courses — `Content/Training/*.json`

```json
{
  "version": "2026.09.1",
  "courses": [
    {
      "key": "ignition-fundamentals",
      "category": "Ignition",
      "title": "Ignition Systems",
      "summary": "One or two sentences.",
      "level": "Beginner",
      "lessons": [
        {
          "key": "ignition-coil-on-plug",
          "title": "Coil-on-plug ignition",
          "minutes": 12,
          "diagram": "coil-on-plug",
          "safety": ["ignition-voltage"],
          "body": "Markdown lesson body (400–900 words). Use ## headings, lists, **bold**, and a 'Key takeaways' section."
        }
      ],
      "quiz": {
        "title": "Ignition Systems Quiz",
        "questions": [
          { "q": "Question?", "choices": ["A", "B", "C", "D"], "answer": 1, "explanation": "Why B is correct." }
        ]
      },
      "flashcards": [ { "front": "Term or question", "back": "Answer" } ],
      "exercises": [ "ohms-law", "voltage-drop" ]
    }
  ]
}
```

* `category` must be one of the training categories: `Fundamentals, Engine,
  FuelSystems, Ignition, Electrical, Diagnostics, Cooling, Hvac, Brakes,
  Suspension, Steering, Transmission, CanBus, Adas, Hybrid, Ev`.
* `level`: `Beginner`, `Intermediate`, `Advanced`.
* `diagram` (optional) refers to `Content/Diagrams/<key>.svg`.
* `answer` is the zero-based index into `choices`.
* `exercises` are keys of built-in parametric exercise generators:
  `ohms-law`, `series-resistance`, `parallel-resistance`, `power-law`,
  `voltage-drop`, `fuel-trim`, `unit-pressure`, `unit-torque`, `unit-temperature`.

---

## 4. Apprentice scenarios — `Content/Scenarios/*.json`

Scenarios are **fictional training cases**. Their readings are illustrative values
chosen for teaching, not specifications for the named vehicle, and the app labels
them that way.

```json
{
  "version": "2026.09.1",
  "scenarios": [
    {
      "key": "camry-lean-pcv",
      "title": "Rough when warm",
      "category": "FuelSystems",
      "difficulty": "Intermediate",
      "vehicle": { "year": 2016, "make": "Toyota", "model": "Camry", "engine": "2.5L I4" },
      "mileage": 128000,
      "complaint": "Vehicle runs rough after warming up.",
      "customerStatement": "What the customer says, in their words.",
      "codes": ["P0171"],
      "freezeFrame": { "RPM": "720", "ECT": "88 °C", "STFT B1": "+18%", "LTFT B1": "+22%" },
      "rootCause": "Split PCV hose at the valve cover elbow.",
      "rootCauseKeywords": ["pcv", "vacuum leak", "hose"],
      "tests": [
        {
          "key": "fuel-trim-review",
          "title": "Review live fuel trims at idle and 2500 RPM",
          "keywords": ["fuel trim", "live data", "stft", "ltft", "scan tool", "data"],
          "result": "What the apprentice observes when performing this test.",
          "value": "high",
          "points": 15,
          "teaches": "Why this test is valuable (shown in the debrief)."
        }
      ],
      "idealPath": ["fuel-trim-review", "visual-inspection", "smoke-test"],
      "debrief": "Explanation of the efficient diagnostic path and reasoning.",
      "commonMistakes": ["Replacing the O2 sensor because the code mentions the sensor system."]
    }
  ]
}
```

* `value`: `high` (efficient, discriminating), `medium`, `low` (little value),
  `parts-cannon` (replacing parts without verification; negative points).
* Each scenario needs 8–14 tests, including at least two tempting-but-poor choices.

---

## 5. Lesson diagrams — `Content/Diagrams/*.svg`

Plain SVG 1.1, `viewBox` based, no external references, no scripts. Use
`currentColor`-free explicit colors that read on both dark and light backgrounds
(the viewer places diagrams on a neutral card). Include `<text>` labels.
