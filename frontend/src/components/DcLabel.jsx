import { getRowDistributionCentreName } from "../utils/distributionCentre";

function DcLabel({ row, value, className = "" }) {
  const label = value ? String(value).trim() : getRowDistributionCentreName(row);
  return <span className={`dc-label ${className}`.trim()}>{label || "Unknown DC"}</span>;
}

export default DcLabel;
