/** Derived-field function registry (spec §4.4). Adding a function is a deliberate code change. */
export const COMPUTED_FNS: Record<string, (inputs: unknown[]) => number | null> = {
  bmi: (inputs) => {
    const [heightCm, weightKg] = inputs.map(Number);
    if (!heightCm || !weightKg || Number.isNaN(heightCm) || Number.isNaN(weightKg) || heightCm <= 0) return null;
    const m = heightCm / 100;
    return Math.round((weightKg / (m * m)) * 10) / 10;
  },
};
