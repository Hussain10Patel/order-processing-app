import { useMemo } from "react";

function MultiDcFilter({
  label = "Distribution Centres",
  distributionCentres = [],
  selectedIds = [],
  onChange,
}) {
  const selectedSet = useMemo(() => new Set(selectedIds.map((value) => Number(value))), [selectedIds]);

  function toggle(id) {
    if (typeof onChange !== "function") {
      return;
    }

    const normalizedId = Number(id);
    const next = selectedSet.has(normalizedId)
      ? selectedIds.filter((value) => Number(value) !== normalizedId)
      : [...selectedIds, normalizedId];

    onChange(next);
  }

  function clearAll() {
    if (typeof onChange === "function") {
      onChange([]);
    }
  }

  return (
    <div>
      <label>{label}</label>
      <details className="multi-dc-dropdown">
        <summary>
          {selectedIds.length > 0 ? `${selectedIds.length} selected` : "All distribution centres"}
        </summary>
        <div className="multi-dc-options">
          {distributionCentres.map((centre) => {
            const checked = selectedSet.has(Number(centre.id));
            return (
              <label key={centre.id} className="multi-dc-option">
                <input
                  type="checkbox"
                  checked={checked}
                  onChange={() => toggle(centre.id)}
                  style={{ width: "auto" }}
                />
                <span>{centre.name}</span>
              </label>
            );
          })}
        </div>
      </details>
      {selectedIds.length > 0 && (
        <div className="multi-dc-chip-row">
          {distributionCentres
            .filter((centre) => selectedSet.has(Number(centre.id)))
            .map((centre) => (
              <button
                type="button"
                key={centre.id}
                className="multi-dc-chip"
                onClick={() => toggle(centre.id)}
              >
                {centre.name} x
              </button>
            ))}
          <button type="button" className="secondary" onClick={clearAll}>
            Clear
          </button>
        </div>
      )}
    </div>
  );
}

export default MultiDcFilter;
