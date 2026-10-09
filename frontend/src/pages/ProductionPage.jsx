import { useEffect, useMemo, useState } from "react";
import { getDistributionCentres, getProductionAssignmentOrders, setOrderDeliveryDate, unassignProductionOrder } from "../services/api";
import StatusLabel from "../components/StatusLabel";

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
          setDeliveryDateDrafts(Object.fromEntries(nextOrders.map((order) => [
            order.id,
            order.deliveryDate || order.originalCsvDeliveryDate || "",
          ])));
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

  useEffect(() => {
    let current = true;
    async function refreshStatuses() {
      try {
        const response = await getProductionAssignmentOrders({
          assignment,
          distributionCentreIds: selectedIdsKey ? selectedIdsKey.split(",") : [],
          orderNumber, orderDateFrom, orderDateTo, deliveryDateFrom, deliveryDateTo,
        });
        if (!current) return;
        const latestById = new Map(response.map((order) => [order.id, order]));
        setOrders((previous) => previous.map((order) => {
          const latest = latestById.get(order.id);
          return latest ? { ...order, deliveryDate: latest.deliveryDate, status: latest.status, isScheduled: latest.isScheduled, isAssignedToProduction: latest.isAssignedToProduction } : order;
        }));
      } catch (requestError) {
        if (current) setError(requestError.message || "Unable to refresh order statuses");
      }
    }
    const interval = setInterval(refreshStatuses, 60000);
    window.addEventListener("focus", refreshStatuses);
    return () => {
      current = false;
      clearInterval(interval);
      window.removeEventListener("focus", refreshStatuses);
    };
  }, [assignment, selectedIdsKey, orderNumber, orderDateFrom, orderDateTo, deliveryDateFrom, deliveryDateTo]);

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

  async function unassignOrder(order) {
    setSavingOrderIds((current) => ({ ...current, [order.id]: true }));
    setError("");
    try {
      await unassignProductionOrder(order.id);
      setRefreshToken((current) => current + 1);
    } catch (requestError) {
      setError(requestError.message || "Failed to unassign order");
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
        <p>Confirm the scheduled delivery date before assigning an approved order. Assignment automatically sets the order to Scheduled.</p>
      </header>

      <div className="panel production-filters-panel" style={{ marginBottom: 16 }}>
        <div className="production-filters-grid">
          <div>
            <label htmlFor="production-assignment-filter">Assignment</label>
            <select id="production-assignment-filter" value={assignment} onChange={(event) => setAssignment(event.target.value)}>
              <option value="all">All</option>
              <option value="assigned">Assigned</option>
              <option value="unassigned">Not Assigned</option>
            </select>
          </div>

          <div>
            <label>Distribution Centre</label>
            <details className="production-dc-dropdown">
              <summary>
                {loadingCentres ? "Loading DCs..." : dcSelectionLabel}
              </summary>
              <div className="production-dc-options">
                <label>
                  <input
                    type="checkbox"
                    checked={selectedDistributionCentreIds.length === 0}
                    onChange={() => setSelectedDistributionCentreIds([])}
                  />
                  All DCs
                </label>
                {distributionCentres.map((centre) => (
                  <label key={centre.id}>
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

          <div>
            <label htmlFor="production-order-number">Order Number</label>
            <input
              id="production-order-number"
              type="search"
              placeholder="Search order number"
              value={orderNumber}
              onChange={(event) => setOrderNumber(event.target.value)}
            />
          </div>

          <fieldset className="production-date-filter">
            <legend>Order Date</legend>
            <div className="production-date-fields">
              <label>From<input aria-label="Order Date From" type="date" value={orderDateFrom} onChange={(event) => setOrderDateFrom(event.target.value)} /></label>
              <label>To<input aria-label="Order Date To" type="date" value={orderDateTo} onChange={(event) => setOrderDateTo(event.target.value)} /></label>
            </div>
          </fieldset>

          <fieldset className="production-date-filter">
            <legend>Delivery Date</legend>
            <div className="production-date-fields">
              <label>From<input aria-label="Delivery Date From" type="date" value={deliveryDateFrom} onChange={(event) => setDeliveryDateFrom(event.target.value)} /></label>
              <label>To<input aria-label="Delivery Date To" type="date" value={deliveryDateTo} onChange={(event) => setDeliveryDateTo(event.target.value)} /></label>
            </div>
          </fieldset>

          <button type="button" className="secondary production-clear-filters" onClick={clearFilters}>Clear Filters</button>
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
                  <th>Original CSV Delivery Date</th>
                  <th>Status</th>
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
                    <td>{formatDate(order.originalCsvDeliveryDate)}</td>
                    <td><StatusLabel status={order.status} /></td>
                    <td>
                      <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
                        <span className={order.isAssignedToProduction ? "badge green" : "badge orange"}>
                          {order.isAssignedToProduction ? "Assigned" : "Not Assigned"}
                        </span>
                        {(!order.isAssignedToProduction || order.status === "Approved") && (
                          <>
                            <input
                              type="date"
                              aria-label={`Scheduled Delivery Date for ${order.orderNumber}`}
                              min={order.orderDate}
                              value={deliveryDateDrafts[order.id] || ""}
                              onChange={(event) => setDeliveryDateDrafts((current) => ({ ...current, [order.id]: event.target.value }))}
                            />
                            <button
                              type="button"
                              className="btn-success"
                              disabled={Boolean(savingOrderIds[order.id]) || !deliveryDateDrafts[order.id]}
                              onClick={() => void saveDeliveryDate(order, deliveryDateDrafts[order.id])}
                            >
                              {savingOrderIds[order.id] ? "Saving..." : "Confirm & Assign"}
                            </button>
                          </>
                        )}
                        {order.isAssignedToProduction && (
                          <button
                            type="button"
                            className="secondary"
                            disabled={Boolean(savingOrderIds[order.id]) || ["EnRoute", "Delivered"].includes(order.status)}
                            onClick={() => void unassignOrder(order)}
                          >
                            Unassign
                          </button>
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