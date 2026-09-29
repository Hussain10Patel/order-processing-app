import { useEffect, useMemo, useState } from "react";
import { getDistributionCentres, getProductionAssignmentOrders, setOrderDeliveryDate } from "../services/api";

function formatDate(value) {
  if (!value) return "-";
  const parsed = new Date(`${value}T00:00:00`);
  return Number.isNaN(parsed.getTime()) ? "-" : parsed.toLocaleDateString();
}

function ProductionPage() {
  const [orders, setOrders] = useState([]);
  const [distributionCentres, setDistributionCentres] = useState([]);
  const [assignment, setAssignment] = useState("all");
  const [selectedDistributionCentreIds, setSelectedDistributionCentreIds] = useState([]);
  const [orderNumber, setOrderNumber] = useState("");
  const [orderDateFrom, setOrderDateFrom] = useState("");
  const [orderDateTo, setOrderDateTo] = useState("");
  const [deliveryDateFrom, setDeliveryDateFrom] = useState("");
  const [deliveryDateTo, setDeliveryDateTo] = useState("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [loadingCentres, setLoadingCentres] = useState(false);
  const [savingOrderIds, setSavingOrderIds] = useState({});
  const [deliveryDateDrafts, setDeliveryDateDrafts] = useState({});
  const [refreshToken, setRefreshToken] = useState(0);

  const selectedIdsKey = useMemo(
    () => [...selectedDistributionCentreIds].sort((left, right) => left - right).join(","),
    [selectedDistributionCentreIds]
  );

  useEffect(() => {
    let current = true;

    async function loadCentres() {
      setLoadingCentres(true);
      try {
        const response = await getDistributionCentres();
        if (current) setDistributionCentres(Array.isArray(response) ? response : response?.items || []);
      } catch (requestError) {
        if (current) setError(requestError.message || "Failed loading distribution centres");
      } finally {
        if (current) setLoadingCentres(false);
      }
    }

    void loadCentres();
    return () => { current = false; };
  }, []);

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError("");

    const filters = {
      assignment,
      distributionCentreIds: selectedIdsKey ? selectedIdsKey.split(",") : [],
      orderNumber,
      orderDateFrom,
      orderDateTo,
      deliveryDateFrom,
      deliveryDateTo,
    };

    getProductionAssignmentOrders(filters)
      .then((response) => {
        if (current) {
          const nextOrders = Array.isArray(response) ? response : [];
          setOrders(nextOrders);
          setDeliveryDateDrafts(Object.fromEntries(nextOrders.map((order) => [order.id, order.deliveryDate || ""])));
        }
      })
      .catch((requestError) => {
        if (current) {
          setOrders([]);
          setError(requestError.message || "Failed loading approved orders");
        }
      })
      .finally(() => {
        if (current) setLoading(false);
      });

    return () => { current = false; };
  }, [assignment, selectedIdsKey, orderNumber, orderDateFrom, orderDateTo, deliveryDateFrom, deliveryDateTo, refreshToken]);

  function toggleDistributionCentre(id) {
    const numericId = Number(id);
    setSelectedDistributionCentreIds((current) => current.includes(numericId)
      ? current.filter((selectedId) => selectedId !== numericId)
      : [...current, numericId]);
  }

  function clearFilters() {
    setAssignment("all");
    setSelectedDistributionCentreIds([]);
    setOrderNumber("");
    setOrderDateFrom("");
    setOrderDateTo("");
    setDeliveryDateFrom("");
    setDeliveryDateTo("");
  }

  async function saveDeliveryDate(order, deliveryDate) {
    setSavingOrderIds((current) => ({ ...current, [order.id]: true }));
    setError("");

    try {
      await setOrderDeliveryDate(order.id, deliveryDate || null);
      setRefreshToken((current) => current + 1);
    } catch (requestError) {
      setError(requestError.message || "Failed updating production assignment");
    } finally {
      setSavingOrderIds((current) => ({ ...current, [order.id]: false }));
    }
  }

  const dcSelectionLabel = selectedDistributionCentreIds.length === 0
    ? "All DCs"
    : `${selectedDistributionCentreIds.length} selected`;

  return (
    <section>
      <header className="page-header">
        <h2>Production</h2>
        <p>Assign approved orders to production.</p>
      </header>

      <div className="panel" style={{ marginBottom: 16 }}>
        <div className="section-heading" style={{ alignItems: "flex-start", flexWrap: "wrap", gap: 16 }}>
          <div style={{ minWidth: 180 }}>
            <label htmlFor="production-assignment-filter">Assignment</label>
            <select id="production-assignment-filter" value={assignment} onChange={(event) => setAssignment(event.target.value)}>
              <option value="all">All</option>
              <option value="assigned">Assigned</option>
              <option value="unassigned">Not Assigned</option>
            </select>
          </div>

          <div style={{ minWidth: 220 }}>
            <label>Distribution Centre</label>
            <details style={{ position: "relative" }}>
              <summary className="secondary" style={{ cursor: "pointer", padding: "8px 12px" }}>
                {loadingCentres ? "Loading DCs..." : dcSelectionLabel}
              </summary>
              <div className="panel" style={{ position: "absolute", zIndex: 2, top: "100%", left: 0, minWidth: "100%", maxHeight: 240, overflowY: "auto", padding: 10 }}>
                <label style={{ display: "flex", gap: 8, whiteSpace: "nowrap" }}>
                  <input
                    type="checkbox"
                    checked={selectedDistributionCentreIds.length === 0}
                    onChange={() => setSelectedDistributionCentreIds([])}
                  />
                  All DCs
                </label>
                {distributionCentres.map((centre) => (
                  <label key={centre.id} style={{ display: "flex", gap: 8, whiteSpace: "nowrap" }}>
                    <input
                      type="checkbox"
                      checked={selectedDistributionCentreIds.includes(Number(centre.id))}
                      onChange={() => toggleDistributionCentre(centre.id)}
                    />
                    {centre.name}
                  </label>
                ))}
              </div>
            </details>
          </div>

          <div style={{ minWidth: 220 }}>
            <label htmlFor="production-order-number">Order Number</label>
            <input
              id="production-order-number"
              type="search"
              placeholder="Search order number"
              value={orderNumber}
              onChange={(event) => setOrderNumber(event.target.value)}
            />
          </div>

          <fieldset style={{ border: 0, padding: 0, margin: 0 }}>
            <legend>Order Date</legend>
            <div style={{ display: "flex", gap: 8 }}>
              <label>From<input aria-label="Order Date From" type="date" value={orderDateFrom} onChange={(event) => setOrderDateFrom(event.target.value)} /></label>
              <label>To<input aria-label="Order Date To" type="date" value={orderDateTo} onChange={(event) => setOrderDateTo(event.target.value)} /></label>
            </div>
          </fieldset>

          <fieldset style={{ border: 0, padding: 0, margin: 0 }}>
            <legend>Delivery Date</legend>
            <div style={{ display: "flex", gap: 8 }}>
              <label>From<input aria-label="Delivery Date From" type="date" value={deliveryDateFrom} onChange={(event) => setDeliveryDateFrom(event.target.value)} /></label>
              <label>To<input aria-label="Delivery Date To" type="date" value={deliveryDateTo} onChange={(event) => setDeliveryDateTo(event.target.value)} /></label>
            </div>
          </fieldset>

          <button type="button" className="secondary" onClick={clearFilters}>Clear Filters</button>
        </div>
      </div>

      {error && <p className="alert error" role="alert">{error}</p>}

      <div className="panel">
        {loading ? (
          <p className="status-text" role="status">Loading approved orders...</p>
        ) : error ? null : orders.length === 0 ? (
          <p className="status-text">No approved orders match the selected filters.</p>
        ) : (
          <div className="table-wrap">
            <table className="orders-table" style={{ width: "100%" }}>
              <thead>
                <tr>
                  <th>Order Number</th>
                  <th>Distribution Centre</th>
                  <th>Order Date</th>
                  <th>Delivery Date</th>
                  <th>Assignment / Delivery Date</th>
                </tr>
              </thead>
              <tbody>
                {orders.map((order) => (
                  <tr key={order.id}>
                    <td>{order.orderNumber}</td>
                    <td>{order.distributionCentreName}</td>
                    <td>{formatDate(order.orderDate)}</td>
                    <td>{formatDate(order.deliveryDate)}</td>
                    <td>
                      <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
                        <span className={order.isAssigned ? "badge green" : "badge orange"}>
                          {order.isAssigned ? "Assigned" : "Not Assigned"}
                        </span>
                        <input
                          type="date"
                          aria-label={`Delivery Date for ${order.orderNumber}`}
                          value={deliveryDateDrafts[order.id] || ""}
                          onChange={(event) => setDeliveryDateDrafts((current) => ({ ...current, [order.id]: event.target.value }))}
                        />
                        <button
                          type="button"
                          className="btn-success"
                          disabled={Boolean(savingOrderIds[order.id]) || !deliveryDateDrafts[order.id]}
                          onClick={() => void saveDeliveryDate(order, deliveryDateDrafts[order.id])}
                        >
                          {savingOrderIds[order.id] ? "Saving..." : order.isAssigned ? "Save Date" : "Assign Date"}
                        </button>
                        {order.isAssigned && !order.isScheduled && (
                          <button
                            type="button"
                            className="secondary"
                            disabled={Boolean(savingOrderIds[order.id])}
                            onClick={() => {
                              setDeliveryDateDrafts((current) => ({ ...current, [order.id]: "" }));
                              void saveDeliveryDate(order, null);
                            }}
                          >
                            Clear Date
                          </button>
                        )}
                        {order.isAssigned && order.isScheduled && (
                          <span className="status-text">Unschedule before clearing date</span>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  );
}

export default ProductionPage;