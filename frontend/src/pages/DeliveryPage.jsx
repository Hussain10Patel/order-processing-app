import { useEffect, useState } from "react";
import DataTable from "../components/DataTable";
import StatusBlock from "../components/StatusBlock";
import { formatDate, getDeliveries, getOrders, getUnscheduledOrders, scheduleDelivery } from "../services/api";

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
  if (!d) return getToday();
  return new Date(d).toISOString().split("T")[0];
}

function DeliveryPage() {
  const [date, setDate] = useState(getToday());
  const [rows, setRows] = useState([]);
  const [unscheduledRows, setUnscheduledRows] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [warningMessage, setWarningMessage] = useState("");
  const [submitting, setSubmitting] = useState(false);

  const [form, setForm] = useState({
    orderNumber: "",
    deliveryDate: getToday(),
    notes: "",
  });

  async function loadSchedule(dateValue) {
    setLoading(true);
    setError("");

    try {
      const normalizedDate = toYMD(dateValue);
      console.log("Selected date:", normalizedDate);
      const [deliveryData, ordersData] = await Promise.all([
        getDeliveries({ date: normalizedDate }),
        getUnscheduledOrders({ date: normalizedDate }),
      ]);
      console.log("Deliveries returned:", deliveryData);

      const scheduled = Array.isArray(deliveryData) ? deliveryData : deliveryData?.data ?? [];
      setRows(scheduled);

      const scheduledOrderIds = new Set(scheduled.map((r) => r.orderId));
      const allOrders = Array.isArray(ordersData) ? ordersData : ordersData?.items ?? ordersData?.data ?? [];
      const unscheduled = allOrders.filter(
        (o) => toYMD(o.deliveryDate) === normalizedDate && !scheduledOrderIds.has(o.id)
      );
      setUnscheduledRows(unscheduled);
    } catch (requestError) {
      setRows([]);
      setUnscheduledRows([]);
      setError(requestError.message || "Failed loading delivery schedule");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    loadSchedule(date);
  }, [date]);

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
      setDate(schedulingDate);
      await loadSchedule(schedulingDate);
    } catch (submitError) {
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
        <div style={{ fontSize: 11, color: "#888", marginBottom: 6 }}>Selected: {date}</div>

        <StatusBlock
          loading={loading}
          error={error}
          empty={!loading && !error && rows.length === 0}
          loadingText="Loading deliveries..."
          emptyText="No deliveries found"
          spinner
        />

        {!loading && !error && rows.length > 0 && (
          <DataTable
            columns={[
              { key: "orderId", header: "Order ID" },
              { key: "orderNumber", header: "Order Number" },
              { key: "distributionCentre", header: "Distribution Centre" },
              { key: "deliveryDate", header: "Delivery Date", render: (row) => (<><span>{formatDate(row.deliveryDate)}</span><div style={{fontSize:10,color:"#888"}}>Row Date: {row.deliveryDate?.slice(0,10)}</div></>) },
              { key: "status", header: "Status" },
              { key: "totalPallets", header: "Total Pallets" },
              { key: "notes", header: "Notes", render: (row) => row.notes || "-" },
            ]}
            data={rows}
            rowKey="id"
            sortKey=""
            sortDirection="asc"
            onSort={() => {}}
          />
        )}

        {!loading && !error && rows.length === 0 && (
          <p className="status-text">No data found</p>
        )}
      </div>

      <div className="panel">
        <h3 style={{ marginBottom: 8 }}>Unscheduled Orders</h3>

        {loading && <p className="status-text">Loading...</p>}

        {!loading && unscheduledRows.length === 0 && (
          <p className="status-text">All orders for this date are scheduled</p>
        )}

        {!loading && unscheduledRows.length > 0 && (
          <DataTable
            columns={[
              { key: "orderNumber", header: "Order Number" },
              {
                key: "distributionCentreName",
                header: "DC",
                render: (row) => row.distributionCentreName || "-",
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
                  <span className="badge orange">{row.statusLabel || "Not Scheduled"}</span>
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
            data={unscheduledRows}
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
