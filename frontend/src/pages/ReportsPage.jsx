import { useEffect, useMemo, useState } from "react";
import DataTable from "../components/DataTable";
import DcLabel from "../components/DcLabel";
import MultiDcFilter from "../components/MultiDcFilter";
import StatusLabel from "../components/StatusLabel";
import StatusBlock from "../components/StatusBlock";
import {
  downloadExport,
  formatCurrency,
  formatDate,
  getReportDates,
  getReportSummary,
} from "../services/api";

function getToday() {
  return new Date().toISOString().slice(0, 10);
}

function toYMD(value) {
  if (typeof value === "string" && /^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return value;
  }
  return new Date(value).toISOString().slice(0, 10);
}

function isReportEmpty(report) {
  if (!report) {
    return true;
  }

  const totalOrders = Number(report.totalOrders ?? 0);
  const totalValue = Number(report.totalValue ?? 0);
  const ordersByStatus = Array.isArray(report.ordersByStatus) ? report.ordersByStatus : [];
  const salesByProduct = Array.isArray(report.salesByProduct) ? report.salesByProduct : [];
  const deliverySummary = Array.isArray(report.deliverySummary) ? report.deliverySummary : [];

  return (
    totalOrders === 0 &&
    totalValue === 0 &&
    ordersByStatus.length === 0 &&
    salesByProduct.length === 0 &&
    deliverySummary.length === 0
  );
}

function ReportsPage() {
  const [date, setDate] = useState(getToday());
  const [report, setReport] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [exporting, setExporting] = useState("");
  const [exportError, setExportError] = useState("");
  const [selectedDistributionCentreIds, setSelectedDistributionCentreIds] = useState([]);
  const [reportDates, setReportDates] = useState([]);

  useEffect(() => {
    async function loadReportDates() {
      try {
        const dates = await getReportDates();
        setReportDates(Array.isArray(dates) ? dates : []);
      } catch {
        // non-critical — page still works without the dropdown
      }
    }
    void loadReportDates();
  }, []);

  useEffect(() => {
    async function loadReports() {
      setLoading(true);
      setError("");

      try {
        const formattedDate = toYMD(date);
        const response = await getReportSummary(formattedDate);
        setReport(response);
      } catch (requestError) {
        console.error("Failed to load reports:", requestError);
        setError(requestError.message || "Unable to load reports");
        setReport(null);
      } finally {
        setLoading(false);
      }
    }

    void loadReports();
  }, [date]);

  useEffect(() => {
    setSelectedDistributionCentreIds([]);
  }, [date]);

  const ordersByStatus = Array.isArray(report?.ordersByStatus) ? report.ordersByStatus : [];
  const salesByProduct = Array.isArray(report?.salesByProduct) ? report.salesByProduct : [];
  const deliverySummary = Array.isArray(report?.deliverySummary) ? report.deliverySummary : [];
  const totalOrders = Number(report?.totalOrders ?? 0);
  const totalValue = Number(report?.totalValue ?? 0);

  const deliveryCentres = useMemo(() => {
    const seen = new Map();
    let nextId = 1;
    deliverySummary.forEach((row) => {
      const name = row.dc ?? row.distributionCentre ?? "";
      if (name && !seen.has(name)) {
        seen.set(name, nextId++);
      }
    });
    return Array.from(seen.entries()).map(([name, id]) => ({ id, name }));
  }, [deliverySummary]);

  const filteredDeliverySummary = useMemo(() => {
    if (!selectedDistributionCentreIds.length) return deliverySummary;
    const selectedNames = new Set(
      deliveryCentres
        .filter((dc) => selectedDistributionCentreIds.includes(dc.id))
        .map((dc) => dc.name)
    );
    return deliverySummary.filter((row) => {
      const name = row.dc ?? row.distributionCentre ?? "";
      return selectedNames.has(name);
    });
  }, [deliverySummary, selectedDistributionCentreIds, deliveryCentres]);

  async function handleExport(type) {
    setExporting(type);
    setExportError("");
    try {
      await downloadExport(type, toYMD(date));
    } catch (requestError) {
      setExportError(requestError.message || "Export failed. Please try again.");
    } finally {
      setExporting("");
    }
  }

  return (
    <section>
      <header className="page-header">
        <h2>Reports</h2>
        <p>Review order totals, product sales, and delivery summaries by date.</p>
      </header>

      <div className="panel">
        <div className="section-heading">
          <div style={{ display: "flex", gap: 12, flexWrap: "wrap", alignItems: "flex-end" }}>
            <div style={{ minWidth: 220 }}>
              <label>Report Date</label>
              <input type="date" value={date} onChange={(event) => setDate(event.target.value)} />
            </div>
            {reportDates.length > 0 && (
              <div style={{ minWidth: 200 }}>
                <label>Available Dates</label>
                <select
                  value={date}
                  onChange={(event) => setDate(event.target.value)}
                >
                  <option value="">— select a date —</option>
                  {reportDates.map((entry) => (
                    <option key={entry.date} value={entry.date}>
                      {entry.date}
                      {entry.hasOrders ? " ✓" : ""}
                    </option>
                  ))}
                </select>
              </div>
            )}
          </div>
          <div className="action-row">
            <button type="button" className="secondary" onClick={() => handleExport("orders")} disabled={exporting !== ""}>
              {exporting === "orders" ? "Exporting..." : "Export Orders"}
            </button>
            <button type="button" className="secondary" onClick={() => handleExport("delivery")} disabled={exporting !== ""}>
              {exporting === "delivery" ? "Exporting..." : "Export Delivery"}
            </button>
            <button type="button" className="secondary" onClick={() => handleExport("pastel")} disabled={exporting !== ""}>
              {exporting === "pastel" ? "Exporting..." : "Export Pastel"}
            </button>
          </div>
        </div>

        {exportError && <p className="alert error">{exportError}</p>}

        <StatusBlock
          loading={loading}
          error={error}
          empty={!loading && !error && isReportEmpty(report)}
          loadingText="Loading reports..."
          emptyText="No report data found for this date."
          spinner
        />
      </div>

      {!loading && !error && (
        <>
          <div className="stats-grid">
            <div className="panel stat-card">
              <h3>Total Orders</h3>
              <strong>{totalOrders}</strong>
            </div>
            <div className="panel stat-card">
              <h3>Total Order Value</h3>
              <strong>{formatCurrency(totalValue)}</strong>
            </div>
          </div>

          <div className="grid-2 panels-grid">
            <div className="panel">
              <h3>Orders by Status</h3>
              {ordersByStatus.length > 0 ? (
                <DataTable
                  columns={[
                    { key: "status", header: "Status" },
                    { key: "count", header: "Count" },
                  ]}
                  data={ordersByStatus}
                  rowKey="id"
                  sortKey=""
                  sortDirection="asc"
                  onSort={() => {}}
                />
              ) : (
                <p className="status-text">No data found</p>
              )}
            </div>

            <div className="panel">
              <h3>Sales by Product</h3>
              {salesByProduct.length > 0 ? (
                <DataTable
                  columns={[
                    { key: "product", header: "Product" },
                    { key: "quantity", header: "Quantity" },
                    { key: "value", header: "Revenue", render: (row) => formatCurrency(row.value) },
                  ]}
                  data={salesByProduct}
                  rowKey="id"
                  sortKey=""
                  sortDirection="asc"
                  onSort={() => {}}
                />
              ) : (
                <p className="status-text">No data found</p>
              )}
            </div>
          </div>

          <div className="panel">
            <h3>Delivery Summary</h3>
            <div style={{ marginBottom: 10 }}>
              <MultiDcFilter
                label="Filter Delivery Summary By Distribution Centres"
                distributionCentres={deliveryCentres}
                selectedIds={selectedDistributionCentreIds}
                onChange={setSelectedDistributionCentreIds}
              />
            </div>
            {filteredDeliverySummary.length > 0 ? (
              <DataTable
                columns={[
                  { key: "poNumber", header: "Order Number" },
                  {
                    key: "dc",
                    header: "Distribution Centre",
                    render: (row) => <DcLabel value={row.dc ?? row.distributionCentre} />,
                  },
                  { key: "deliveryDate", header: "Delivery Date", render: (row) => formatDate(row.deliveryDate) },
                  {
                    key: "status",
                    header: "Status",
                    render: (row) => <StatusLabel label={row.status} />,
                  },
                ]}
                data={filteredDeliverySummary.map((delivery, index) => ({
                  ...delivery,
                  id: delivery.id ?? `${delivery.poNumber}-${delivery.deliveryDate}-${index}`,
                }))}
                rowKey="id"
                sortKey=""
                sortDirection="asc"
                onSort={() => {}}
              />
            ) : (
              <p className="status-text">No deliveries found</p>
            )}
          </div>
        </>
      )}
    </section>
  );
}

export default ReportsPage;