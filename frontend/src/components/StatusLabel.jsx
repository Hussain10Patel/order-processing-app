import { getStatusLabel } from "../services/api";

function toStatusText(status, fallback = "Unknown") {
  if (status === null || status === undefined || status === "") {
    return fallback;
  }

  if (typeof status === "string" && Number.isNaN(Number(status))) {
    return status;
  }

  return getStatusLabel(status);
}

function getStatusClassName(statusText) {
  const normalized = String(statusText).toLowerCase().replace(/[^a-z0-9]/g, "");

  if (normalized.includes("inproduction") || normalized === "6") {
    return "status-chip status-in-production";
  }

  if (normalized.includes("processed") || normalized === "5") {
    return "status-chip status-processed";
  }

  if (normalized.includes("approved") || normalized.includes("validated") || normalized === "4") {
    return "status-chip status-approved";
  }

  if (normalized.includes("scheduled") || normalized.includes("delivered")) {
    return "status-chip";
  }

  if (normalized.includes("flag") || normalized.includes("pending")) {
    return "status-chip warning";
  }

  return "status-chip";
}

function StatusLabel({ status, label }) {
  const statusText = String(label ?? toStatusText(status));
  return <span className={getStatusClassName(statusText)}>{statusText}</span>;
}

export default StatusLabel;
