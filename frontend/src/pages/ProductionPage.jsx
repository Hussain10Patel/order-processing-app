import { useEffect, useMemo, useState } from "react";
import DataTable from "../components/DataTable";
import StatusBlock from "../components/StatusBlock";
import { getProduction } from "../services/api";

const STOCK_STORAGE_KEY = "productionStock";

function getToday() {
  return new Date().toISOString().slice(0, 10);
}

function toYMD(date) {
  return new Date(date).toISOString().split("T")[0];
}

function toNumber(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function getRowStockKey(row) {
  return `${row.productId ?? row.productCode ?? "product"}-${row.distributionCentreId ?? row.distributionCentreName ?? row.distributionCentre ?? "dc"}`;
}

function readSavedStock() {
  try {
    return JSON.parse(localStorage.getItem(STOCK_STORAGE_KEY) || "{}");
  } catch {
    return {};
  }
}

function normalizeRows(rawRows, savedStock) {
  return rawRows.map((row, index) => {
    const totalQuantity = toNumber(row.totalQuantity ?? row.quantity);
    const totalPallets = toNumber(row.totalPallets ?? row.pallets);
    const stockKey = getRowStockKey(row);
    const openingStock = toNumber(savedStock[stockKey] ?? 0);
    const productionRequired = Math.max(0, totalQuantity - openingStock);

    return {
      ...row,
      id:
        row.id ??
        `${row.productId ?? row.productCode ?? "product"}-${row.distributionCentreId ?? row.distributionCentreName ?? row.distributionCentre ?? index}`,
      stockKey,
      totalQuantity,
      totalPallets,
      openingStock,
      productionRequired,
    };
  });
}

function normalizeUnscheduled(rawRows) {
  return rawRows.map((row, index) => {
    const totalQuantity = toNumber(row.totalQuantity ?? row.quantity);
    const totalPallets = toNumber(row.totalPallets ?? row.pallets);
    return {
      ...row,
      id:
        row.id ??
        `unscheduled-${row.productId ?? row.productCode ?? "product"}-${row.distributionCentreId ?? row.distributionCentreName ?? row.distributionCentre ?? index}`,
      totalQuantity,
      totalPallets,
      openingStock: 0,
      productionRequired: totalQuantity,
    };
  });
}

function sortByShortage(rows) {
  return [...rows].sort((a, b) => toNumber(b.productionRequired) - toNumber(a.productionRequired));
}

function computeTotals(rows) {
  return rows.reduce(
    (acc, row) => {
      acc.totalQuantity += toNumber(row.totalQuantity);
      acc.totalPallets += toNumber(row.totalPallets);
      acc.totalProductionRequired += toNumber(row.productionRequired);
      return acc;
    },
    { totalQuantity: 0, totalPallets: 0, totalProductionRequired: 0 }
  );
}

function renderApprovalBadge(status) {
  const normalized = String(status || "").toLowerCase();

  if (normalized === "validated") {
    return (
      <span className="badge orange" title="Order is not fully approved yet">
        Not Approved
      </span>
    );
  }

  if (normalized === "approved") {
    return <span className="badge green">Approved</span>;
  }

  if (normalized === "processed") {
    return <span className="badge green">Processed</span>;
  }

  return <span className="badge yellow">Unknown</span>;
}

function renderProductionBadge(productionRequired) {
  return toNumber(productionRequired) > 0 ? (
    <span className="badge orange">Shortage</span>
  ) : (
    <span className="badge green">OK</span>
  );
}

function ProductionSection({ title, badge, helperText, rows, onUpdateStock, faded }) {
  const totals = useMemo(() => computeTotals(rows), [rows]);

  const isUnscheduled = faded;

  const columns = [
    {
      key: "product",
      header: "Product",
      render: (row) => {
        const name = row.productName || row.product || "Unknown";
        const code = row.productCode ? ` (${row.productCode})` : "";
        return `${name}${code}`;
      },
    },
    {
      key: "distributionCentre",
      header: "Distribution Centre",
      render: (row) => row.distributionCentreName || row.distributionCentre || "-",
    },
    {
      key: "totalQuantity",
      header: "Total Quantity",
      render: (row) => toNumber(row.totalQuantity).toFixed(0),
    },
    {
      key: "totalPallets",
      header: "Total Pallets",
      render: (row) => toNumber(row.totalPallets).toFixed(2),
    },
    {
      key: "openingStock",
      header: "Opening Stock",
      render: (row, index) =>
        isUnscheduled ? (
          <input type="number" value={row.openingStock} disabled style={{ opacity: 0.4 }} />
        ) : (
          <input
            type="number"
            value={row.openingStock}
            onChange={(event) => onUpdateStock(index, event.target.value)}
          />
        ),
    },
    {
      key: "productionRequired",
      header: isUnscheduled ? "Potential Required" : "Production Required",
      render: (row) => Math.max(0, toNumber(row.productionRequired)).toFixed(0),
    },
    {
      key: "approvalStatus",
      header: "Approval Status",
      render: (row) => renderApprovalBadge(row.status),
    },
    {
      key: "productionStatus",
      header: "Production Status",
      render: (row) => renderProductionBadge(row.productionRequired),
    },
  ];

  return (
    <div className="panel" style={faded ? { opacity: 0.75 } : {}}>
      <div className="section-heading">
        <h3>{title}</h3>
        {badge}
      </div>
      <p className="status-text" style={{ marginBottom: 8 }}>{helperText}</p>

      {rows.length === 0 ? (
        <p className="status-text">No records found</p>
      ) : (
        <>
          <DataTable
            columns={columns}
            data={rows}
            rowKey="id"
            rowClassName={(row) => (toNumber(row.productionRequired) > 0 ? "row-shortage" : "")}
            sortKey=""
            sortDirection="asc"
            onSort={() => {}}
          />
          <div style={{ marginTop: 10 }}>
            <p className="status-text">Total Quantity: {totals.totalQuantity.toFixed(0)}</p>
            <p className="status-text">Total Pallets: {totals.totalPallets.toFixed(2)}</p>
            <p className="status-text">
              {isUnscheduled ? "Total Potential Required" : "Total Production Required"}:{" "}
              {totals.totalProductionRequired.toFixed(0)}
            </p>
          </div>
        </>
      )}
    </div>
  );
}

function ProductionPage() {
  const [date, setDate] = useState(getToday());
  const [scheduledRows, setScheduledRows] = useState([]);
  const [unscheduledRows, setUnscheduledRows] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [savedStock, setSavedStock] = useState(readSavedStock);

  async function loadProduction(selectedDate) {
    setLoading(true);
    setError("");

    try {
      const formattedDate = toYMD(selectedDate);

      console.log("[UI] Selected date:", selectedDate);
      console.log("[API CALL] Date sent:", formattedDate);

      const res = await getProduction(formattedDate);

      console.log("[API RESPONSE]", res);
      if (!res || (!res.scheduled?.length && !res.unscheduled?.length)) {
        console.warn("[EMPTY DATA] No production data returned for date:", formattedDate);
      }
      console.log("Scheduled rows:", res.scheduled);
      console.log("Unscheduled rows:", res.unscheduled);

      const normalizedScheduled = sortByShortage(normalizeRows(res.scheduled || [], savedStock));
      const normalizedUnscheduled = normalizeUnscheduled(res.unscheduled || []);

      console.log("[SCHEDULED NORMALIZED]", normalizedScheduled);
      console.log("[UNSCHEDULED NORMALIZED]", normalizedUnscheduled);

      setScheduledRows(normalizedScheduled);
      setUnscheduledRows(normalizedUnscheduled);
    } catch (requestError) {
      setScheduledRows([]);
      setUnscheduledRows([]);
      setError(requestError.message || "Failed loading production");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    loadProduction(date);
  }, [date]);

  useEffect(() => {
    if (!loading && !error && scheduledRows.length === 0 && unscheduledRows.length === 0) {
      console.log("[EMPTY CHECK]", {
        scheduledLength: scheduledRows.length,
        unscheduledLength: unscheduledRows.length,
      });
    }
  }, [scheduledRows, unscheduledRows, loading, error]);

  function makeUpdateStock(setter) {
    return function updateStock(index, value) {
      const parsed = Number(value);
      const openingStock = Number.isFinite(parsed) ? parsed : 0;

      setter((current) => {
        const targetRow = current[index];
        if (targetRow?.stockKey) {
          setSavedStock((previous) => {
            const updatedStock = { ...previous, [targetRow.stockKey]: openingStock };
            localStorage.setItem(STOCK_STORAGE_KEY, JSON.stringify(updatedStock));
            return updatedStock;
          });
        }

        const updatedRows = current.map((row, rowIndex) => {
          if (rowIndex !== index) return row;
          const productionRequired = Math.max(0, toNumber(row.totalQuantity) - openingStock);
          return { ...row, openingStock, productionRequired };
        });

        return sortByShortage(updatedRows);
      });
    };
  }

  const hasAny = scheduledRows.length > 0 || unscheduledRows.length > 0;

  return (
    <section>
      <header className="page-header">
        <h2>Production Planning</h2>
        <p>Review production demand by delivery date.</p>
        <p className="status-text">Production is based on scheduled delivery dates</p>
      </header>

      <div className="panel">
        <div style={{ maxWidth: 240 }}>
          <label>Production Date</label>
          <input type="date" value={date} onChange={(event) => setDate(event.target.value)} />
        </div>

        <StatusBlock
          loading={loading}
          error={error}
          empty={!loading && !error && !hasAny}
          loadingText="Loading production data..."
          emptyText="No production records found"
          spinner
        />
      </div>

      {!loading && !error && (
        <>
          <ProductionSection
            title="Scheduled Production"
            badge={<span className="badge green" style={{ marginLeft: 8 }}>Confirmed</span>}
            helperText="These orders are scheduled for delivery and must be produced"
            rows={scheduledRows}
            onUpdateStock={makeUpdateStock(setScheduledRows)}
            faded={false}
          />

          <ProductionSection
            title="Unscheduled Demand"
            badge={<span className="badge orange" style={{ marginLeft: 8 }}>Not Scheduled</span>}
            helperText="These orders are not yet scheduled but may require production"
            rows={unscheduledRows}
            onUpdateStock={makeUpdateStock(setUnscheduledRows)}
            faded
          />
        </>
      )}
    </section>
  );
}

export default ProductionPage;
