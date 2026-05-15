export function normalizeCentreName(value) {
  const normalized = String(value ?? "").trim();
  return normalized || "Unknown DC";
}

export function getRowDistributionCentreName(row) {
  return normalizeCentreName(
    row?.distributionCentreName ?? row?.distributionCentre ?? row?.dc ?? row?.name
  );
}

export function getRowDistributionCentreId(row) {
  const rawValue = row?.distributionCentreId ?? row?.dcId ?? row?.id;
  const parsedValue = Number(rawValue);
  return Number.isFinite(parsedValue) ? parsedValue : null;
}

export function toSelectedDcIds(values) {
  if (!Array.isArray(values)) {
    return [];
  }

  return values
    .map((value) => Number(value))
    .filter((value, index, array) => Number.isFinite(value) && array.indexOf(value) === index);
}

export function hasAnySelectedDc(selectedDcIds) {
  return Array.isArray(selectedDcIds) && selectedDcIds.length > 0;
}

export function rowMatchesSelectedDcs(row, selectedDcIds) {
  if (!hasAnySelectedDc(selectedDcIds)) {
    return true;
  }

  const rowId = getRowDistributionCentreId(row);
  if (rowId !== null) {
    return selectedDcIds.includes(rowId);
  }

  const rowName = getRowDistributionCentreName(row).toLowerCase();
  return selectedDcIds.some((selectedId) => String(selectedId).toLowerCase() === rowName);
}
