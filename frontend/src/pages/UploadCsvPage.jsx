import { useState } from "react";
import StatusBlock from "../components/StatusBlock";
import {
  retryCsvImport,
  uploadCsv,
} from "../services/api";

const TEMPLATE_HEADERS = [
  "Order Number",
  "Order Date",
  "Delivery Date",
  "Distribution Centre",
  "Product",
  "Quantity",
  "Price",
];

const RESULT_FIELDS = new Set([
  "success",
  "totalRows",
  "createdOrders",
  "skippedOrders",
  "updatedOrders",
  "flaggedOrders",
  "fileId",
  "type",
  "message",
  "errors",
  "validationErrors",
  "requiresUserAction",
  "missingDistributionCentres",
  "missingProducts",
  "createdDistributionCentres",
  "productsNeedingPricing",
]);

function getUploadSummary(result) {
  const createdOrders = result?.createdOrders ?? 0;
  const skippedOrders = result?.skippedOrders ?? 0;
  return `${createdOrders} orders created, ${skippedOrders} skipped`;
}

function formatValidationError(errorItem, index) {
  const fileName = errorItem?.fileName ? `[${errorItem.fileName}] ` : "";
  const rowText = errorItem?.rowNumber ? `Row ${errorItem.rowNumber}: ` : "";
  return `${fileName}${rowText}${errorItem?.message ?? `Validation error ${index + 1}`}`;
}

function getValidationSummary(validationErrors) {
  const failures = new Set(
    (validationErrors ?? []).map((item, index) => `${item?.fileName ?? "file"}:${item?.rowNumber ?? index}`)
  );
  const count = failures.size;
  return count === 1 ? "1 row failed" : `${count} rows failed`;
}

function isQuantityValidationError(errorItem) {
  return errorItem?.field === "Quantity";
}

function isPriceValidationError(errorItem) {
  return errorItem?.field === "Price";
}

function isMissingFieldValidationError(errorItem) {
  const message = String(errorItem?.message ?? "").toLowerCase();
  return message.includes("required") || message.includes("missing required column");
}

function getErrorSeverity(errorItem) {
  if (isPriceValidationError(errorItem)) {
    return "price";
  }

  if (isMissingFieldValidationError(errorItem)) {
    return "missing";
  }

  if (isQuantityValidationError(errorItem)) {
    return "quantity";
  }

  return "default";
}

function getErrorBadgeLabel(errorItem) {
  if (isPriceValidationError(errorItem)) {
    return "[!] Price";
  }

  if (isMissingFieldValidationError(errorItem)) {
    return "[!] Missing Field";
  }

  if (isQuantityValidationError(errorItem)) {
    return "[!] Quantity";
  }

  return "[!] Validation";
}

function getRawData(result) {
  if (!result || typeof result !== "object") {
    return null;
  }

  const metadata = Object.fromEntries(
    Object.entries(result).filter(([key, value]) => !RESULT_FIELDS.has(key) && value !== undefined)
  );

  return Object.keys(metadata).length > 0 ? metadata : null;
}

function getSummaryTone(result) {
  return (result?.createdOrders ?? 0) > 0 ? "success" : "warning";
}

function withCreatedCentres(result, createdCentres) {
  return {
    ...result,
    createdDistributionCentres: createdCentres,
  };
}

function withProductsNeedingPricing(result, productsNeedingPricing) {
  return {
    ...result,
    productsNeedingPricing,
  };
}

function normalizeProductLabel(product) {
  return String(product ?? "").trim();
}

function formatConfiguredProductLabel(product) {
  const label = normalizeProductLabel(product);
  if (!label) {
    return "";
  }

  const unmappedMatch = label.match(/^UNMAPPED PRODUCT\s*-\s*(.+)$/i);
  if (unmappedMatch) {
    return unmappedMatch[1].trim();
  }

  return label;
}

function getProductsUsingCsvPricing(result) {
  const fromPricingList = (result?.productsNeedingPricing ?? [])
    .map(formatConfiguredProductLabel)
    .filter(Boolean);

  const fromWarnings = (result?.errors ?? []).flatMap((item) => {
    const warningMatch = String(item).match(/Price not configured in system for product '([^']+)'/i);
    if (!warningMatch) {
      return [];
    }

    const formatted = formatConfiguredProductLabel(warningMatch[1]);
    return formatted ? [formatted] : [];
  });

  return [...new Set([...fromPricingList, ...fromWarnings])];
}

function getActualProcessingErrors(result) {
  return (result?.errors ?? []).filter(
    (item) => !/Price not configured in system for product '([^']+)'/i.test(String(item))
  );
}

function hasActionRequired(result) {
  return Boolean(result?.requiresUserAction) && (
    (result?.missingProducts?.length ?? 0) > 0 ||
    (result?.missingDistributionCentres?.length ?? 0) > 0
  );
}

function UploadCsvPage() {
  const [files, setFiles] = useState([]);
  const [allowDuplicates, setAllowDuplicates] = useState(false);
  const [loading, setLoading] = useState(false);
  const [progress, setProgress] = useState(0);
  const [currentFile, setCurrentFile] = useState("");
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [result, setResult] = useState(null);
  const [showActionRequiredModal, setShowActionRequiredModal] = useState(false);
  const [resolvingAction, setResolvingAction] = useState(false);
  const [showValidationDetails, setShowValidationDetails] = useState(false);
  const [showRawData, setShowRawData] = useState(false);
  const [showPricingWarningDetails, setShowPricingWarningDetails] = useState(false);

  async function handleSubmit(event) {
    event.preventDefault();
    if (!files.length) {
      setError("Select at least one CSV file");
      return;
    }

    setLoading(true);
    setProgress(0);
    setCurrentFile("");
    setError("");
    setMessage("");
    setResult(null);
    setShowActionRequiredModal(false);
    setShowValidationDetails(false);
    setShowRawData(false);
    setShowPricingWarningDetails(false);

    try {
      const response = await uploadCsv(files, {
        allowDuplicates,
        onProgress: (value, fileName) => {
          setProgress(value);
          setCurrentFile(fileName);
        },
      });
      setResult(response);

      if (hasActionRequired(response)) {
        setShowActionRequiredModal(true);
        setMessage(response.message || "Resolve the missing references to continue the import.");
        return;
      }

      setFiles([]);
      setMessage(
        (response?.createdOrders ?? 0) > 0
          ? getUploadSummary(response)
          : "No valid rows processed. Please check errors below."
      );
    } catch (uploadError) {
      setError(uploadError.message || "Upload failed");
    } finally {
      setLoading(false);
    }
  }

  async function handleCreateAndContinue() {
    if (!result?.fileId) {
      setShowActionRequiredModal(false);
      return;
    }

    const missingProducts = result?.missingProducts ?? [];

    setResolvingAction(true);
    setLoading(true);
    setProgress(0);
    setCurrentFile("");
    setError("");
    setMessage("Creating missing references and retrying import...");

    try {
      const response = await retryCsvImport(result.fileId, {
        createMissingProducts: true,
        createMissingDistributionCentres: true,
      });

      const nextResult = withProductsNeedingPricing(response, missingProducts);
      setResult(nextResult);
      setShowActionRequiredModal(false);
      setShowPricingWarningDetails(false);

      if (hasActionRequired(nextResult)) {
        setShowActionRequiredModal(true);
        setMessage(nextResult.message || "Some references still need attention before the import can finish.");
        return;
      }

      setFiles([]);
      setProgress(100);
      setMessage(
        (response?.createdOrders ?? 0) > 0
          ? getUploadSummary(response)
          : response.message || "No valid rows processed. Please check errors below."
      );
    } catch (requestError) {
      setError(requestError.message || "Failed to retry CSV import");
    } finally {
      setResolvingAction(false);
      setLoading(false);
    }
  }

  function handleCancelUpload() {
    setShowActionRequiredModal(false);
    setMessage("Upload cancelled. Missing references were not created.");
  }

  function handleDownloadTemplate() {
    const csvContent = `${TEMPLATE_HEADERS.join(",")}\n`;
    const blob = new Blob([csvContent], { type: "text/csv;charset=utf-8" });
    const url = window.URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = "order-upload-template.csv";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    window.URL.revokeObjectURL(url);
  }

  const validationErrors = result?.validationErrors ?? [];
  const rawData = getRawData(result);
  const pricingWarningProducts = getProductsUsingCsvPricing(result);
  const processingErrors = getActualProcessingErrors(result);

  return (
    <section>
      <header className="page-header">
        <h2>CSV Order Import</h2>
        <p>Upload one or more CSV files and review processing summary.</p>
      </header>

      <div className="panel">
        <form onSubmit={handleSubmit}>
          <div className="upload-toolbar">
            <p className="status-text upload-note">
              Supports CSV and pipe-delimited files. Prices are read from the file.
            </p>
            <button type="button" className="secondary upload-template-button" onClick={handleDownloadTemplate}>
              Download Template
            </button>
          </div>

          <div>
            <label>CSV Files</label>
            <input
              type="file"
              multiple
              accept=".csv,text/csv"
              onChange={(event) => setFiles(Array.from(event.target.files ?? []))}
            />
          </div>

          <div style={{ marginTop: 10 }}>
            <label>
              <input
                type="checkbox"
                checked={allowDuplicates}
                onChange={(event) => setAllowDuplicates(event.target.checked)}
                style={{ width: "auto", marginRight: 8 }}
              />
              Allow duplicate orders
            </label>
          </div>

          <div style={{ marginTop: 12 }}>
            <button type="submit" disabled={loading}>
              {loading ? "Uploading..." : "Upload CSV"}
            </button>
          </div>

          {loading && (
            <div style={{ marginTop: 10 }}>
              <p className="status-text">
                Upload progress: {progress}% {currentFile ? `(${currentFile})` : ""}
              </p>
              <div className="progress-track">
                <div className="progress-fill" style={{ width: `${progress}%` }} />
              </div>
            </div>
          )}
        </form>

        <StatusBlock loading={loading} error={error} spinner loadingText={message || "Uploading files..."} />

        {message && result && <p className={`alert ${getSummaryTone(result)}`}>{message}</p>}

        {result && (
          <div className="upload-results">
            <h3>Upload Summary</h3>
            <div className="upload-summary-grid">
              <div className="panel stat-card upload-stat-card success">
                <span>Total Rows</span>
                <strong>{result.totalRows ?? 0}</strong>
              </div>
              <div className="panel stat-card upload-stat-card success">
                <span>Created Orders</span>
                <strong>{result.createdOrders ?? 0}</strong>
              </div>
              <div className="panel stat-card upload-stat-card success">
                <span>Skipped Orders</span>
                <strong>{result.skippedOrders ?? 0}</strong>
              </div>
              <div className="panel stat-card upload-stat-card success">
                <span>Flagged Orders</span>
                <strong>{result.flaggedOrders ?? 0}</strong>
              </div>
            </div>

            <p className="status-text upload-summary-copy"><strong>{getUploadSummary(result)}</strong></p>
            {(result.createdOrders ?? 0) === 0 && (
              <p className="alert warning">No valid rows processed. Please check errors below.</p>
            )}

            <div className="upload-validation-panel">
              <h4>Validation Issues</h4>
              {validationErrors.length ? (
                <div className="validation-summary-panel">
                  <div className="validation-summary-row">
                    <p className="status-text validation-summary-copy">
                      <strong>{getValidationSummary(validationErrors)}</strong>
                    </p>
                    <button
                      type="button"
                      className="secondary validation-toggle-button"
                      onClick={() => setShowValidationDetails((value) => !value)}
                    >
                      {showValidationDetails ? "Hide Details" : "View Details"}
                    </button>
                  </div>

                  {validationErrors.some(isPriceValidationError) && (
                    <p className="alert warning validation-alert">[!] Invalid or missing price in CSV file</p>
                  )}

                  {validationErrors.some(isMissingFieldValidationError) && (
                    <p className="alert error validation-alert">[!] Missing required fields detected in the upload</p>
                  )}

                  {validationErrors.some(isQuantityValidationError) && (
                    <p className="alert error validation-alert">[!] Invalid quantity in uploaded file</p>
                  )}

                  {showValidationDetails && (
                    <div className="validation-issues-container">
                      <ul className="error-list validation-issues-list">
                        {validationErrors.map((item, index) => {
                          const severity = getErrorSeverity(item);

                          return (
                            <li
                              key={`validation-error-${item.fileName ?? "file"}-${item.rowNumber ?? index}-${index}`}
                              className={`validation-issue-row ${severity}`}
                            >
                              <span className={`validation-badge ${severity}`}>{getErrorBadgeLabel(item)}</span>
                              <span>{formatValidationError(item, index)}</span>
                            </li>
                          );
                        })}
                      </ul>
                    </div>
                  )}
                </div>
              ) : (
                <p className="status-text">No row validation errors</p>
              )}
            </div>

            {result.createdDistributionCentres?.length > 0 && (
              <div className="upload-created-centres-panel">
                <h4>Newly Created Distribution Centres</h4>
                <div className="created-centres-list">
                  {result.createdDistributionCentres.map((centre) => (
                    <span key={centre} className="status-chip success">
                      {centre}
                    </span>
                  ))}
                </div>
              </div>
            )}

            {result.productsNeedingPricing?.length > 0 && (
              <div className="upload-created-centres-panel">
                <h4>Products Requiring Pricing</h4>
                <div className="created-centres-list">
                  {result.productsNeedingPricing.map((product) => (
                    <span key={product} className="attention-chip">
                      <strong>{product}</strong>
                      <span className="attention-chip-badge">⚠ Needs Pricing</span>
                    </span>
                  ))}
                </div>
              </div>
            )}

            {pricingWarningProducts.length > 0 && (
              <div className="upload-warning-summary-panel">
                <h4>Product Pricing Summary</h4>
                <div className="alert warning upload-warning-summary-copy">
                  {pricingWarningProducts.length} product{pricingWarningProducts.length === 1 ? " is" : "s are"} using CSV pricing (no system price configured)
                </div>
                <div className="validation-summary-row">
                  <p className="status-text warning-text upload-warning-helper-copy">
                    Prices from the CSV were used successfully. You can configure these products later in Admin.
                  </p>
                  <button
                    type="button"
                    className="secondary validation-toggle-button"
                    onClick={() => setShowPricingWarningDetails((value) => !value)}
                  >
                    {showPricingWarningDetails ? "Hide products" : "Show products"}
                  </button>
                </div>
                {showPricingWarningDetails && (
                  <ul className="upload-warning-product-list">
                    {pricingWarningProducts.map((product) => (
                      <li key={product}>{product}</li>
                    ))}
                  </ul>
                )}
              </div>
            )}

            <div>
              <h4>Order Processing Notes</h4>
              {processingErrors.length ? (
                <ul className="error-list">
                  {processingErrors.map((item, index) => (
                    <li key={`error-${index}`}>{item}</li>
                  ))}
                </ul>
              ) : (
                <p className="status-text">No failures</p>
              )}
            </div>

            {rawData && (
              <div className="upload-raw-panel">
                <div className="validation-summary-row">
                  <h4>Raw Data</h4>
                  <button
                    type="button"
                    className="secondary validation-toggle-button"
                    onClick={() => setShowRawData((value) => !value)}
                  >
                    {showRawData ? "Hide Raw Data" : "View Raw Data"}
                  </button>
                </div>
                {showRawData && <pre className="raw-data-block">{JSON.stringify(rawData, null, 2)}</pre>}
              </div>
            )}
          </div>
        )}
      </div>

      {showActionRequiredModal && hasActionRequired(result) && (
        <div className="modal-overlay" role="presentation">
          <div className="modal-card action-required-card" role="dialog" aria-modal="true" aria-labelledby="action-required-title">
            <h3 id="action-required-title">Action Required</h3>
            <p>Some rows reference products or distribution centres that do not exist yet. Create the missing records and continue the import.</p>

            <div className="modal-section-grid">
              {result?.missingProducts?.length > 0 && (
                <section className="modal-list-card">
                  <h4>Missing Products</h4>
                  <ul className="missing-centres-list">
                    {result.missingProducts.map((product) => (
                      <li key={product}>{product}</li>
                    ))}
                  </ul>
                </section>
              )}

              {result?.missingDistributionCentres?.length > 0 && (
                <section className="modal-list-card">
                  <h4>Missing Distribution Centres</h4>
                  <ul className="missing-centres-list">
                    {result.missingDistributionCentres.map((centre) => (
                      <li key={centre}>{centre}</li>
                    ))}
                  </ul>
                </section>
              )}
            </div>

            {(result?.missingProducts?.length ?? 0) > 0 && (
              <p className="alert warning modal-warning-copy">
                Missing products will be created as placeholder products and will need pricing before export.
              </p>
            )}

            <div className="modal-actions">
              <button type="button" onClick={handleCreateAndContinue} disabled={resolvingAction}>
                {resolvingAction ? "Retrying..." : "Create Missing & Continue"}
              </button>
              <button type="button" className="secondary" onClick={handleCancelUpload} disabled={resolvingAction}>
                Cancel Upload
              </button>
            </div>
          </div>
        </div>
      )}
    </section>
  );
}

export default UploadCsvPage;
