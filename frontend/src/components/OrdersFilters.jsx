import { getStatusLabel } from "../services/api";
import MultiDcFilter from "./MultiDcFilter";

function OrdersFilters({
  search,
  onSearch,
  productCode,
  onProductCode,
  productName,
  onProductName,
  status,
  onStatus,
  statuses,
  selectedDistributionCentreIds,
  onSelectedDistributionCentreIds,
  distributionCentres,
  fromDate,
  onFromDate,
  toDate,
  onToDate,
  onClear,
}) {
  return (
    <div className="toolbar">
      <div>
        <label>Order Number</label>
        <input
          type="text"
          value={search}
          onChange={(event) => onSearch(event.target.value)}
          placeholder="Search order number"
        />
      </div>

      <div>
        <label>Product Code</label>
        <input
          type="text"
          value={productCode}
          onChange={(event) => onProductCode(event.target.value)}
          placeholder="Search product code"
        />
      </div>

      <div>
        <label>Product Name</label>
        <input
          type="text"
          value={productName}
          onChange={(event) => onProductName(event.target.value)}
          placeholder="Search product name"
        />
      </div>

      <div>
        <label>Status</label>
        <select value={status} onChange={(event) => onStatus(event.target.value)}>
          <option value="All">All</option>
          {statuses.map((value) => (
            <option key={value} value={String(value)}>
              {getStatusLabel(value)}
            </option>
          ))}
        </select>
      </div>

      <MultiDcFilter
        label="Distribution Centres"
        distributionCentres={distributionCentres}
        selectedIds={selectedDistributionCentreIds}
        onChange={onSelectedDistributionCentreIds}
      />

      <div>
        <label>From Date</label>
        <input
          type="date"
          value={fromDate}
          onChange={(event) => onFromDate(event.target.value)}
        />
      </div>

      <div>
        <label>To Date</label>
        <input
          type="date"
          value={toDate}
          onChange={(event) => onToDate(event.target.value)}
        />
      </div>

      <div>
        <label>Actions</label>
        <button type="button" className="secondary" onClick={onClear}>
          Clear filters
        </button>
      </div>
    </div>
  );
}

export default OrdersFilters;
