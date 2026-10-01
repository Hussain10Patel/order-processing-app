import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import MultiDcFilter from "../components/MultiDcFilter";
import { formatCurrency, getDashboardOverview, getDistributionCentres, getStatusLabel } from "../services/api";

function getToday() {
  return new Date().toISOString().slice(0, 10);
}

function getDaysAgo(days) {
  const date = new Date();
  date.setDate(date.getDate() - days);
  return date.toISOString().slice(0, 10);
}

const statusOptions = [1, 2, 3, 4, 5, 6, 7];

function DashboardPage() {
  const navigate = useNavigate();
  const [filters, setFilters] = useState({
    fromDate: getDaysAgo(30),
    toDate: getToday(),
    orderNumber: "",
    productCode: "",
    productName: "",
    distributionCentreIds: [],
    orderStatus: "",
    productionAssignment: "",
    deliveryStatus: "",
    exception: "",
  });
  const [centres, setCentres] = useState([]);
  const [dashboard, setDashboard] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  useEffect(() => {
    void getDistributionCentres()
      .then((response) => setCentres(Array.isArray(response) ? response : []))
      .catch(() => setCentres([]));
  }, []);

  useEffect(() => {
    let cancelled = false;
    async function loadDashboard() {
      setLoading(true);
      setError("");
      try {
        const response = await getDashboardOverview(filters);
        if (!cancelled) setDashboard(response);
      } catch (requestError) {
        if (!cancelled) setError(requestError.message || "Unable to load dashboard data.");
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    void loadDashboard();
    return () => { cancelled = true; };
  }, [filters]);

  function updateFilter(name, value) {
    setFilters((current) => ({ ...current, [name]: value }));
  }

  function clearFilters() {
    setFilters({
      fromDate: "", toDate: "", orderNumber: "", productCode: "", productName: "",
      distributionCentreIds: [], orderStatus: "", productionAssignment: "", deliveryStatus: "", exception: "",
    });
  }

  const orders = dashboard?.orders ?? [];
  const centreSummary = dashboard?.distributionCentres ?? [];
  const attention = dashboard?.requiresAttention ?? [];
  const products = dashboard?.products ?? [];

  return (
    <section className="dashboard-page">
      <header className="page-header">
        <h2>Dashboard</h2>
        <p>Operational snapshot across orders, production, and delivery readiness.</p>
      </header>

      <div className="panel dashboard-filters-panel">
        <div className="section-heading"><h3>Dashboard Filters</h3></div>
        <div className="dashboard-filters-grid">
          <div><label>From Date</label><input type="date" value={filters.fromDate} onChange={(event) => updateFilter("fromDate", event.target.value)} /></div>
          <div><label>To Date</label><input type="date" value={filters.toDate} onChange={(event) => updateFilter("toDate", event.target.value)} /></div>
          <div><label>Order Number</label><input value={filters.orderNumber} onChange={(event) => updateFilter("orderNumber", event.target.value)} /></div>
          <div><label>Product Code</label><input value={filters.productCode} onChange={(event) => updateFilter("productCode", event.target.value)} /></div>
          <div><label>Product Name</label><input value={filters.productName} onChange={(event) => updateFilter("productName", event.target.value)} /></div>
          <MultiDcFilter label="Distribution Centres" distributionCentres={centres} selectedIds={filters.distributionCentreIds} onChange={(value) => updateFilter("distributionCentreIds", value)} />
          <div><label>Order Status</label><select value={filters.orderStatus} onChange={(event) => updateFilter("orderStatus", event.target.value)}><option value="">Any</option>{statusOptions.map((status) => <option key={status} value={status}>{getStatusLabel(status)}</option>)}</select></div>
          <div><label>Production Assignment</label><select value={filters.productionAssignment} onChange={(event) => updateFilter("productionAssignment", event.target.value)}><option value="">Any</option><option value="assigned">Assigned</option><option value="unassigned">Not Assigned</option></select></div>
          <div><label>Delivery Status</label><select value={filters.deliveryStatus} onChange={(event) => updateFilter("deliveryStatus", event.target.value)}><option value="">Any</option><option value="unscheduled">Unscheduled</option><option value="scheduled">Scheduled</option></select></div>
          <div><label>Exceptions</label><select value={filters.exception} onChange={(event) => updateFilter("exception", event.target.value)}><option value="">Any</option><option value="not assigned">Not Assigned</option><option value="flagged">Flagged</option><option value="no price configured">No Price Configured</option><option value="pricing issue">Pricing Issue</option><option value="overdue">Overdue</option></select></div>
        </div>
        <button type="button" className="secondary" onClick={clearFilters}>Clear Filters</button>
      </div>

      {error && <p className="alert error">{error}</p>}
      {loading && <p className="status-text">Loading dashboard...</p>}
      {!loading && !error && dashboard && (
        <>
          <div className="stats-grid dashboard-stats-grid">
            {[["Total Orders", dashboard.totalOrders], ["Assigned", dashboard.assignedOrders], ["Not Assigned", dashboard.unassignedOrders], ["Scheduled", dashboard.scheduledOrders], ["Unscheduled", dashboard.unscheduledOrders], ["Flagged", dashboard.flaggedOrders], ["Overdue", dashboard.overdueOrders], ["Total Order Value", formatCurrency(dashboard.totalOrderValue)]].map(([label, value]) => <div className={`panel stat-card${label === "Total Order Value" ? " dashboard-total-value-card" : ""}`} key={label}><span>{label}</span><strong>{value}</strong></div>)}
          </div>

          <div className="panel"><div className="section-heading"><h3>Order Overview</h3></div><div className="table-wrap"><table><thead><tr><th>Order Number</th><th>Distribution Centre</th><th>Order Date</th><th>Delivery Date</th><th>Production Assignment</th><th>Delivery Status</th><th>Quantity</th><th>Pallets</th><th>Total Value</th><th>Status</th><th>Action</th></tr></thead><tbody>{orders.map((order) => <tr key={order.orderNumber}><td>{order.orderNumber}</td><td>{order.distributionCentre}</td><td>{order.orderDate}</td><td>{order.deliveryDate || "-"}</td><td>{order.isAssignedToProduction ? "Assigned" : "Not Assigned"}</td><td>{order.isScheduled ? "Scheduled" : "Unscheduled"}</td><td>{order.totalQuantity}</td><td>{order.totalPallets}</td><td>{formatCurrency(order.totalValue)}</td><td>{order.status}</td><td><button type="button" className="secondary" onClick={() => navigate("/orders", { state: { focusOrderNumber: order.orderNumber, focusToken: Date.now() } })}>View</button></td></tr>)}</tbody></table></div>{orders.length === 0 && <p className="status-text">No orders match the selected filters.</p>}</div>

          <div className="grid-2 dashboard-summary-grid" style={{ marginTop: 14 }}>
            <div className="panel"><h3>Distribution Centre Summary</h3><div className="table-wrap"><table><thead><tr><th>Distribution Centre</th><th>Total Orders</th><th>Assigned</th><th>Not Assigned</th><th>Scheduled</th><th>Unscheduled</th><th>Total Value</th></tr></thead><tbody>{centreSummary.map((row) => <tr key={row.distributionCentre}><td>{row.distributionCentre}</td><td>{row.totalOrders}</td><td>{row.assigned}</td><td>{row.notAssigned}</td><td>{row.scheduled}</td><td>{row.unscheduled}</td><td>{formatCurrency(row.totalValue)}</td></tr>)}</tbody></table></div></div>
            <div className="panel"><h3>Requires Attention</h3><div className="table-wrap"><table><thead><tr><th>Order</th><th>Centre</th><th>Exception</th><th>Value</th></tr></thead><tbody>{attention.map((row, index) => <tr key={`${row.orderNumber}-${row.exception}-${index}`}><td>{row.orderNumber}</td><td>{row.distributionCentre}</td><td>{row.exception}</td><td>{formatCurrency(row.totalValue)}</td></tr>)}</tbody></table></div>{attention.length === 0 && <p className="status-text">No exceptions in the selected dataset.</p>}</div>
          </div>

          <div className="grid-2 dashboard-summary-grid" style={{ marginTop: 14 }}>
            <div className="panel"><h3>Production Summary</h3><div className="table-wrap"><table><tbody><tr><td>Orders awaiting assignment</td><td>{dashboard.unassignedOrders}</td></tr><tr><td>Assigned orders</td><td>{dashboard.assignedOrders}</td></tr></tbody></table></div></div>
            <div className="panel"><h3>Product / Revenue Summary</h3><div className="table-wrap"><table><thead><tr><th>Product</th><th>SKU</th><th>Quantity</th><th>Revenue</th></tr></thead><tbody>{products.map((row) => <tr key={`${row.productName}-${row.skuCode}`}><td>{row.productName || "Unknown Product"}</td><td>{row.skuCode || "-"}</td><td>{row.totalQuantity}</td><td>{formatCurrency(row.totalRevenue)}</td></tr>)}</tbody></table></div></div>
          </div>
        </>
      )}
    </section>
  );
}

export default DashboardPage;
