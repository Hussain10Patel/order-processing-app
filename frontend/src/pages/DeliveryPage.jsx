import { useEffect, useMemo, useState } from "react";
import DcLabel from "../components/DcLabel";
import DataTable from "../components/DataTable";
import MultiDcFilter from "../components/MultiDcFilter";
import StatusLabel from "../components/StatusLabel";
import StatusBlock from "../components/StatusBlock";
import { formatDate, getDeliveries, getOrders, getUnscheduledDeliveries, scheduleDelivery, unscheduleDelivery } from "../services/api";
import { rowMatchesSelectedDcs } from "../utils/distributionCentre";

async function resolveOrderByNumber(orderNumber) {
  console.log("Resolving order number:", orderNumber);

  const res = await getOrders({
    orderNumber: orderNumber,
    page: 1,
    pageSize: 1,
  });

  const order = res?.data?.[0] || res?.[0];

  if (!order) {
    throw new Error("Order not found");
  }

  console.log("Resolved order:", order);
  return order;
}

function getToday() {
  return new Date().toISOString().slice(0, 10);
}

function toYMD(d) {
  if (!d) return "";
  return new Date(d).toISOString().split("T")[0];
}

function toOrdersArray(payload) {
  if (Array.isArray(payload)) {
    return payload;
  }

  if (Array.isArray(payload?.items)) {
    return payload.items;
  }

  if (Array.isArray(payload?.data)) {
    return payload.data;
  }

  return [];
}

function dedupeRows(rows, getKey) {
  const map = new Map();

  rows.forEach((row) => {
    const key = Number(getKey(row));
    if (!Number.isFinite(key) || map.has(key)) {
      return;
    }

    map.set(key, row);
  });

  return [...map.values()];
}

function DeliveryPage() {
  const [date, setDate] = useState("");
  const [rows, setRows] = useState([]);
  const [unscheduledRows, setUnscheduledRows] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [warningMessage, setWarningMessage] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [unschedulingOrderId, setUnschedulingOrderId] = useState(null);
  const [distributionCentres, setDistributionCentres] = useState([]);
  const [selectedDistributionCentreIds, setSelectedDistributionCentreIds] = useState([]);

  const [form, setForm] = useState({
    orderNumber: "",
    deliveryDate: getToday(),
    notes: "",
  });

  async function loadSchedule() {
    setLoading(true);
    setError("");

    try {
      console.log("Loading all delivery-eligible orders");
      const [deliveryData, unscheduledData] = await Promise.all([
        getDeliveries(),
        getUnscheduledDeliveries(),
      ]);
      console.log("[DELIVERY FETCH RESPONSE] Deliveries:", deliveryData);
      console.log("[DELIVERY FETCH RESPONSE] Unscheduled:", unscheduledData);

      const scheduledRaw = Array.isArray(deliveryData) ? deliveryData : deliveryData?.data ?? [];
      const scheduled = dedupeRows(scheduledRaw, (row) => row.id ?? row.orderId);
      setRows(scheduled);

      const unscheduledOrdersRaw = toOrdersArray(unscheduledData);
      const unscheduledOrders = dedupeRows(unscheduledOrdersRaw, (row) => row.id ?? row.orderId);

      setDistributionCentres(
        [...scheduled, ...unscheduledOrders]
          .map((row) => ({
            id: Number(row.distributionCentreId),
            name: row.distributionCentreName || row.distributionCentre || "Unknown DC",
          }))
          .filter((row, index, array) =>
            Number.isFinite(row.id) &&
            row.name &&
            array.findIndex((item) => item.id === row.id) === index
          )
      );
      setUnscheduledRows(unscheduledOrders);
    } catch (requestError) {
      setRows([]);
      setUnscheduledRows([]);
      setError(requestError.message || "Failed loading delivery schedule");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    loadSchedule();
  }, []);

  useEffect(() => {
    function handleOrdersRefresh() {
      void loadSchedule();
    }

    window.addEventListener("orders:refresh", handleOrdersRefresh);
    return () => {
      window.removeEventListener("orders:refresh", handleOrdersRefresh);
    };
  }, []);

  const filteredByDate = useMemo(() => {
    if (!date) {
      return () => true;
    }

    const selectedDate = toYMD(date);
    return (value) => toYMD(value) === selectedDate;
  }, [date]);

  const filteredRows = useMemo(() => {
    return rows.filter(
      (row) => rowMatchesSelectedDcs(row, selectedDistributionCentreIds) && filteredByDate(row.deliveryDate)
    );
  }, [filteredByDate, rows, selectedDistributionCentreIds]);

  const filteredUnscheduledRows = useMemo(() => {
    return unscheduledRows.filter(
      (row) => rowMatchesSelectedDcs(row, selectedDistributionCentreIds) && filteredByDate(row.deliveryDate)
    );
  }, [filteredByDate, selectedDistributionCentreIds, unscheduledRows]);

  async function submitSchedule(event) {
    event.preventDefault();
    setMessage("");
    setWarningMessage("");
    setSubmitting(true);

    try {
      const order = await resolveOrderByNumber(form.orderNumber);
      console.log("Scheduling order:", order);
      console.log("Order status:", order.status, order.statusLabel);

      if (order.isPriceMismatch) {
        setMessage("❌ Price mismatch must be approved before scheduling");
        return;
      }

      if (order.isPriceMissing) {
        console.warn("Scheduling order with missing price:", order);
        setWarningMessage("⚠ This order has no configured price. Please update pricing.");
      }

      const schedulingDate = toYMD(form.deliveryDate);
      console.log("Scheduling date:", schedulingDate);

      const payload = {
        orderId: order.id,
        deliveryDate: schedulingDate,
        notes: form.notes || null,
      };
      console.log("Scheduling:", payload.orderId, payload.deliveryDate);
      console.log("Submitting payload:", payload);

      await scheduleDelivery(payload);

      setMessage("Delivery scheduled successfully");
      setForm((current) => ({ ...current, orderNumber: "", notes: "" }));
      await loadSchedule();
    } catch (submitError) {
      console.error("Scheduling failed:", submitError);
      if (submitError.message === "Order not found" || submitError.status === 404) {
        setMessage("Order not found");
      } else if (submitError.status === 422) {
        setMessage(submitError.message || "Failed to schedule delivery");
      } else {
        setMessage(submitError.message || "Failed to schedule delivery");
      }
    } finally {
      setSubmitting(false);
    }
  }

  async function handleUnschedule(row) {
    const orderId = Number(row?.orderId);
    if (!Number.isFinite(orderId) || orderId <= 0) {
      setMessage("Invalid order for unscheduling");
      return;
    }

    const confirmed = window.confirm(`Unschedule order ${row.orderNumber || orderId}?`);
    if (!confirmed) {
      return;
    }

    setUnschedulingOrderId(orderId);
    setMessage("");
    setWarningMessage("");

    try {
      const response = await unscheduleDelivery(orderId);
      setMessage(response?.message || "Delivery unscheduled successfully");
      await loadSchedule();
    } catch (requestError) {
      console.error("Unschedule failed:", requestError);
      setMessage(requestError.message || "Failed to unschedule delivery");
    } finally {
      setUnschedulingOrderId(null);
    }
  }

  return (
    <section>
      <header className="page-header">
        <h2>Delivery Scheduling</h2>
        <p>Schedule deliveries and review daily delivery list.</p>
      </header>

      <div className="panel">
        <h3>Schedule Delivery</h3>
        <form onSubmit={submitSchedule}>
          <div className="grid-2">
            <div>
              <label>Order Number</label>
              <input
                required
                type="text"
                value={form.orderNumber}
                placeholder="e.g. 1195505144"
                onChange={(event) =>
                  setForm((current) => ({ ...current, orderNumber: event.target.value }))
                }
              />
            </div>

            <div>
              <label>Delivery Date</label>
              <input
                required
                type="date"
                value={form.deliveryDate}
                onChange={(event) =>
                  setForm((current) => ({ ...current, deliveryDate: event.target.value }))
                }
              />
            </div>
          </div>

          <div style={{ marginTop: 12 }}>
            <label>Notes</label>
            <textarea
              value={form.notes}
              onChange={(event) =>
                setForm((current) => ({ ...current, notes: event.target.value }))
              }
            />
          </div>

          <div style={{ marginTop: 10 }}>
            <button type="submit" disabled={submitting}>
              {submitting ? "Scheduling..." : "Schedule"}
            </button>
          </div>
        </form>

        {warningMessage && <p className="alert warning">{warningMessage}</p>}

        {message && (
          <p className={message.includes("success") ? "alert success" : "alert error"}>
            {message}
          </p>
        )}
      </div>

      <div className="panel">
        <div style={{ maxWidth: 240, marginBottom: 8 }}>
          <label>Daily List Date</label>
          <input type="date" value={date} onChange={(event) => setDate(event.target.value)} />
        </div>
        <div style={{ marginBottom: 10 }}>
          <MultiDcFilter
            label="Filter By Distribution Centres"
            distributionCentres={distributionCentres}
            selectedIds={selectedDistributionCentreIds}
            onChange={setSelectedDistributionCentreIds}
          />
        </div>
        <div style={{ fontSize: 11, color: "#888", marginBottom: 6 }}>
          Selected: {date || "All dates"}
        </div>

        <StatusBlock
          loading={loading}
          error={error}
          empty={!loading && !error && filteredRows.length === 0}
          loadingText="Loading deliveries..."
          emptyText="No scheduled orders match current filters"
          spinner
        />

        {!loading && !error && filteredRows.length > 0 && (
          <DataTable
            columns={[
              { key: "orderId", header: "Order ID" },
              { key: "orderNumber", header: "Order Number" },
              {
                key: "distributionCentre",
                header: "Distribution Centre",
                render: (row) => <DcLabel row={row} />,
              },
              { key: "deliveryDate", header: "Delivery Date", render: (row) => (<><span>{formatDate(row.deliveryDate)}</span><div style={{fontSize:10,color:"#888"}}>Row Date: {row.deliveryDate?.slice(0,10)}</div></>) },
              {
                key: "status",
                header: "Status",
                render: (row) => (
                  <StatusLabel status={row.orderStatus || row.status} label={row.orderStatus || row.status} />
                ),
              },
              { key: "totalPallets", header: "Total Pallets" },
              { key: "notes", header: "Notes", render: (row) => row.notes || "-" },
              {
                key: "action",
                header: "",
                render: (row) => (
                  <button
                    type="button"
                    className="secondary table-action-button"
                    disabled={unschedulingOrderId === row.orderId}
                    onClick={() => {
                      void handleUnschedule(row);
                    }}
                  >
                    {unschedulingOrderId === row.orderId ? "Unscheduling..." : "Unschedule"}
                  </button>
                ),
              },
            ]}
            data={filteredRows}
            rowKey="id"
            sortKey=""
            sortDirection="asc"
            onSort={() => {}}
          />
        )}

        {!loading && !error && filteredRows.length === 0 && (
          <p className="status-text">No scheduled orders match current filters</p>
        )}
      </div>

      <div className="panel">
        <h3 style={{ marginBottom: 8 }}>Unscheduled Orders</h3>

        {loading && <p className="status-text">Loading...</p>}

        {!loading && filteredUnscheduledRows.length === 0 && (
          <p className="status-text">No unscheduled orders match current filters</p>
        )}

        {!loading && filteredUnscheduledRows.length > 0 && (
          <DataTable
            columns={[
              { key: "orderNumber", header: "Order Number" },
              {
                key: "distributionCentreName",
                header: "DC",
                render: (row) => <DcLabel row={row} />,
              },
              {
                key: "deliveryDate",
                header: "Requested Delivery Date",
                render: (row) => formatDate(row.deliveryDate),
              },
              {
                key: "statusLabel",
                header: "Status",
                render: (row) => (
                  <StatusLabel status={row.status} label={row.statusLabel || row.status || "Not Scheduled"} />
                ),
              },
              {
                key: "action",
                header: "",
                render: (row) => (
                  <button
                    type="button"
                    className="secondary table-action-button"
                    onClick={() =>
                      setForm((current) => ({
                        ...current,
                        orderNumber: String(row.orderNumber),
                        deliveryDate: row.deliveryDate?.slice(0, 10) || current.deliveryDate,
                      }))
                    }
                  >
                    Schedule
                  </button>
                ),
              },
            ]}
            data={filteredUnscheduledRows}
            rowKey="id"
            sortKey=""
            sortDirection="asc"
            onSort={() => {}}
          />
        )}
      </div>
    </section>
  );
}

export default DeliveryPage;
