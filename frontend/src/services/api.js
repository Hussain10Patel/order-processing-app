import axios from "axios";
import { formatDate, parseUtcDate } from "../utils/date.js";
export { formatDate, parseUtcDate };

//const API_BASE = "https://order-processing-app-3.onrender.com";
//const API_BASE = "https://order-processing-app-3.onrender.com";
const API_BASE = "http://localhost:8080";
 

function getToday() {
  return new Date().toISOString().slice(0, 10);
}

function normalizeDateFilter(input = getToday()) {
  if (typeof input === "string") {
    return { date: input };
  }

  if (input && typeof input === "object") {
    return input;
  }

  return { date: getToday() };
}

function toQueryString(params) {
  const searchParams = new URLSearchParams();

  Object.entries(params).forEach(([key, value]) => {
    if (value === undefined || value === null || value === "") {
      return;
    }

    searchParams.append(key, String(value));
  });

  const query = searchParams.toString();
  return query ? `?${query}` : "";
}

function extractApiErrorMessage(payload, status) {
  if (typeof payload === "object" && payload) {
    if (typeof payload.message === "string" && payload.message.trim()) {
      return payload.message.trim();
    }

    if (typeof payload.error === "string" && payload.error.trim()) {
      return payload.error.trim();
    }

    if (typeof payload.detail === "string" && payload.detail.trim()) {
      return payload.detail.trim();
    }

    if (typeof payload.title === "string" && payload.title.trim()) {
      return payload.title.trim();
    }

    if (payload.errors && typeof payload.errors === "object") {
      const firstEntry = Object.values(payload.errors).find(
        (value) => Array.isArray(value) && value.length > 0
      );
      if (firstEntry && typeof firstEntry[0] === "string" && firstEntry[0].trim()) {
        return firstEntry[0].trim();
      }
    }
  }

  if (typeof payload === "string" && payload.trim()) {
    return payload.trim();
  }

  if (status >= 500) {
    return "Server error. Please try again.";
  }

  return `Request failed (${status})`;
}

async function handleResponse(response) {
  const contentType = response.headers.get("content-type") ?? "";
  const isJson = contentType.includes("application/json");
  let payload = null;

  try {
    payload = isJson ? await response.json() : await response.text();
  } catch (parseError) {
    console.error("Failed to parse API response:", parseError);
    payload = null;
  }

  if (!response.ok) {
    const message = extractApiErrorMessage(payload, response.status);

    console.error("API request failed:", {
      status: response.status,
      statusText: response.statusText,
      payload,
    });

    const error = new Error(message);
    error.status = response.status;
    error.payload = payload;
    throw error;
  }

  return payload;
}

async function request(path, options = {}) {
  const requestOptions = {
    headers: {
      ...(options.body instanceof FormData ? {} : { "Content-Type": "application/json" }),
      ...(options.headers ?? {}),
    },
    ...options,
  };

  try {
    const response = await fetch(`${API_BASE}${path}`, requestOptions);
    return await handleResponse(response);
  } catch (error) {
    const method = (requestOptions.method ?? "GET").toUpperCase();
    const shouldRetry = method === "GET" && error instanceof TypeError;

    if (shouldRetry) {
      try {
        const retryResponse = await fetch(`${API_BASE}${path}`, requestOptions);
        return await handleResponse(retryResponse);
      } catch (retryError) {
        console.error(`API retry failed for ${method} ${path}:`, retryError);
        throw new Error("Unable to reach the server. Check your connection and try again.");
      }
    }

    if (error instanceof TypeError) {
      console.error(`Network error for ${method} ${path}:`, error);
      throw new Error("Unable to reach the server. Check your connection and try again.");
    }

    throw error;
  }
}

export async function getOrders(filters = {}) {
  return request(`/api/orders${toQueryString(filters)}`, { method: "GET" });
}

export async function getOrderById(id) {
  return request(`/api/orders/${id}`, { method: "GET" });
}

export async function getUnscheduledOrders({ date, status } = {}) {
  return request(`/api/orders${toQueryString({ deliveryDate: date, status })}`, { method: "GET" });
}

export async function createManualOrder(orderData) {
  return request("/api/orders/manual", {
    method: "POST",
    body: JSON.stringify(orderData),
  });
}

export async function getSystemPrice(productId, distributionCentreId) {
  return request(
    `/api/orders/pricing${toQueryString({ productId, distributionCentreId })}`,
    { method: "GET" }
  );
}

export async function adjustOrder(id, payload) {
  return request(`/api/orders/${id}/adjust`, {
    method: "PUT",
    body: JSON.stringify(payload),
  });
}

export async function recalculateOrder(id) {
  return request(`/api/orders/${id}/recalculate`, {
    method: "POST",
  });
}

export async function updateOrderStatus(id, status) {
  const normalizedStatus = String(status).trim().toLowerCase();

  if (normalizedStatus === "approved" || normalizedStatus === "4") {
    return request(`/api/orders/${id}/approve`, {
      method: "POST",
    });
  }

  if (normalizedStatus === "processed" || normalizedStatus === "5") {
    return request(`/api/orders/${id}/process`, {
      method: "POST",
    });
  }

  throw new Error("Unsupported order status update");
}

export async function getProducts() {
  return request("/api/admin/products", { method: "GET" });
}

export async function getStock() {
  return request("/api/stock", { method: "GET" });
}

export async function updateStock(payload) {
  return request("/api/stock/update", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function getDistributionCentres() {
  return request("/api/admin/distributioncentres", { method: "GET" });
}

export async function getAuditLogs() {
  return request("/api/audit", { method: "GET" });
}

export async function getOrderAudit(id) {
  return request(`/api/audit/order/${id}`, { method: "GET" });
}

export async function uploadCsv(files, options = {}) {
  const allowDuplicates = options.allowDuplicates ?? false;
  const createMissingProducts = options.createMissingProducts ?? false;
  const onProgress = options.onProgress;
  const allFiles = Array.from(files ?? []);

  if (allFiles.length === 0) {
    throw new Error("No files selected.");
  }

  const summary = {
    success: true,
    totalRows: 0,
    createdOrders: 0,
    skippedOrders: 0,
    updatedOrders: 0,
    flaggedOrders: 0,
    fileId: null,
    type: null,
    message: "",
    errors: [],
    validationErrors: [],
    requiresUserAction: false,
    missingDistributionCentres: [],
    missingProducts: [],
  };

  for (let index = 0; index < allFiles.length; index += 1) {
    const formData = new FormData();
    formData.append("files", allFiles[index]);

    let payload;

    try {
      const response = await fetch(
        `${API_BASE}/api/upload/csv${toQueryString({ allowDuplicates, createMissingProducts })}`,
        {
          method: "POST",
          body: formData,
        }
      );

      payload = await handleResponse(response);
    } catch (error) {
      if (error instanceof TypeError) {
        console.error("CSV upload network error:", error);
        throw new Error("Unable to upload files. Check your connection and try again.");
      }

      throw error;
    }
    summary.totalRows += payload.totalRows ?? 0;
    summary.createdOrders += payload.createdOrders ?? 0;
    summary.skippedOrders += payload.skippedOrders ?? 0;
    summary.updatedOrders += payload.updatedOrders ?? 0;
    summary.flaggedOrders += payload.flaggedOrders ?? 0;
    summary.success = summary.success && (payload.success ?? true);
    summary.fileId = payload.fileId ?? summary.fileId;
    summary.type = summary.type ?? payload.type ?? null;
    summary.message = summary.message || payload.message || "";
    summary.errors.push(...(payload.errors ?? []));
    summary.validationErrors.push(...(payload.validationErrors ?? []));
    summary.requiresUserAction = summary.requiresUserAction || Boolean(payload.requiresUserAction);
    summary.missingDistributionCentres = [
      ...new Set([
        ...summary.missingDistributionCentres,
        ...(payload.missingDistributionCentres ?? []),
      ]),
    ];
    summary.missingProducts = [
      ...new Set([
        ...summary.missingProducts,
        ...(payload.missingProducts ?? []),
      ]),
    ];

    if (typeof onProgress === "function") {
      onProgress(Math.round(((index + 1) / allFiles.length) * 100), allFiles[index].name);
    }
  }

  return summary;
}

export async function createMissingDistributionCentres(centres) {
  return request("/api/orders/create-missing-distribution-centres", {
    method: "POST",
    body: JSON.stringify({ centres }),
  });
}

export async function retryCsvImport(fileId, options = {}) {
  const createMissingProducts = options.createMissingProducts ?? false;
  const createMissingDistributionCentres = options.createMissingDistributionCentres ?? false;
  const payload = {
    fileId,
    createMissing: createMissingDistributionCentres,
    createMissingProducts,
  };

  console.log("[CSV RETRY] Payload:", payload);

  const response = await request("/api/orders/retry-import", {
    method: "POST",
    body: JSON.stringify(payload),
  });

  console.log("[CSV RETRY] Response:", response);
  return response;
}

export async function getProduction(options = {}) {
  const normalizedOptions =
    typeof options === "string" || options instanceof Date ? { date: options } : options;

  const dateValue = normalizedOptions?.date;
  const query = dateValue ? toQueryString({ date: String(dateValue) }) : "";
  return request(`/api/production${query}`, { method: "GET" });
}

export async function saveProductionDecision(payload) {
  return request("/api/production/decision", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function getDeliveries(date) {
  const query = date === undefined || date === null || date === ""
    ? ""
    : toQueryString(normalizeDateFilter(date));

  return request(`/api/delivery${query}`, { method: "GET" });
}

export async function getUnscheduledDeliveries(date) {
  const query = date === undefined || date === null || date === ""
    ? ""
    : toQueryString(normalizeDateFilter(date));

  return request(`/api/delivery/unscheduled${query}`, {
    method: "GET",
  });
}

export async function getSupplierDeliverySummary(date = getToday()) {
  return request(`/api/reports/supplier-delivery${toQueryString({ date })}`, {
    method: "GET",
  });
}

export async function getDailyDeliveryReport(date = getToday()) {
  return request(`/api/reports/daily-delivery${toQueryString({ date })}`, {
    method: "GET",
  });
}

export async function getOrdersReport() {
  return request("/api/reports/orders", { method: "GET" });
}

export async function getSalesReport() {
  return request("/api/reports/sales", { method: "GET" });
}

export async function getReportDates() {
  return request("/api/reports/available-dates", { method: "GET" });
}

export async function getReportSummary(date = getToday()) {
  return request(`/api/reports/summary-data${toQueryString({ date })}`, { method: "GET" });
}

function getDownloadFileName(contentDisposition, fallback) {
  if (!contentDisposition) {
    return fallback;
  }

  const utf8Match = contentDisposition.match(/filename\*=UTF-8''([^;]+)/i);
  if (utf8Match?.[1]) {
    return decodeURIComponent(utf8Match[1].trim());
  }

  const basicMatch = contentDisposition.match(/filename=\"?([^\";]+)\"?/i);
  if (basicMatch?.[1]) {
    return basicMatch[1].trim();
  }

  return fallback;
}

const EXPORT_ENDPOINT_MAP = {
  orders: "orders",
  delivery: "delivery",
  pastel: "pastel",
};

async function downloadDetailedExport(type, date = getToday()) {
  console.log("[EXPORT CLICK]", type);

  const endpoint = EXPORT_ENDPOINT_MAP[type];
  if (!endpoint) {
    throw new Error(`Invalid export type: ${type}`);
  }

  const url = `${API_BASE}/api/export/${endpoint}${toQueryString({ date })}`;
  console.log("[EXPORT URL]", url);

  const response = await axios.get(url, { responseType: "blob" });
  console.log("[EXPORT STATUS]", response.status);

  const blob = new Blob([response.data], { type: "text/csv" });
  let filename = `${type}-${date}.csv`;

  const disposition = response.headers["content-disposition"];
  filename = getDownloadFileName(disposition, filename);

  const link = document.createElement("a");
  link.href = window.URL.createObjectURL(blob);
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
}

export async function downloadReportExport(type, date = getToday()) {
  return downloadDetailedExport(type, date);
}

export async function downloadExport(type, date = getToday()) {
  return downloadDetailedExport(type, date);
}

export async function scheduleDelivery(payload) {
  return request("/api/delivery/schedule", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function createProduct(payload) {
  return request("/api/admin/products", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function updateProduct(id, payload) {
  return request(`/api/admin/products/${id}`, {
    method: "PUT",
    body: JSON.stringify(payload),
  });
}

export async function deleteProduct(id) {
  return request(`/api/admin/products/${id}`, {
    method: "DELETE",
  });
}

export async function getPriceLists() {
  return request("/api/admin/pricelists", { method: "GET" });
}

export async function upsertPriceList(payload) {
  return request("/api/admin/pricelists", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function getPricePromotions() {
  return request("/api/admin/price-promotions");
}

export async function upsertPricePromotion(payload) {
  return request("/api/admin/price-promotions", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function updatePricePromotion(id, payload) {
  return request(`/api/admin/price-promotions/${id}`, {
    method: "PUT",
    body: JSON.stringify(payload),
  });
}

export async function deletePricePromotion(id) {
  return request(`/api/admin/price-promotions/${id}`, {
    method: "DELETE",
  });
}

export async function deletePriceList(id) {
  console.log("[DELETE REQUEST]", "pricelists", id);

  try {
    const response = await request(`/api/pricelists/${id}`, {
      method: "DELETE",
    });

    console.log("[DELETE RESPONSE]", "success", "pricelists", id);
    return response;
  } catch (error) {
    console.log("[DELETE RESPONSE]", "error", "pricelists", id, error?.message);
    throw error;
  }
}

export async function createDistributionCentre(payload) {
  console.log("🌐 API FUNCTION CALLED with:", payload);
  return request("/api/admin/distributioncentres", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function deleteDistributionCentre(id) {
  console.log("[DELETE REQUEST]", "distributioncentres", id);

  try {
    const response = await request(`/api/distributioncentres/${id}`, {
      method: "DELETE",
    });

    console.log("[DELETE RESPONSE]", "success", "distributioncentres", id);
    return response;
  } catch (error) {
    console.log("[DELETE RESPONSE]", "error", "distributioncentres", id, error?.message);
    throw error;
  }
}

export async function deleteOrder(id) {
  return request(`/api/orders/${id}`, {
    method: "DELETE",
  });
}

export async function resetTestData() {
  return request("/api/admin/reset-data", {
    method: "POST",
  });
}

export const api = {
  getOrders,
  getOrderById,
  createManualOrder,
  getSystemPrice,
  adjustOrder,
  updateOrderStatus,
  getProducts,
  getDistributionCentres,
  getAuditLogs,
  getOrderAudit,
  uploadCsv,
  createMissingDistributionCentres,
  retryCsvImport,
  getProduction,
  getDeliveries,
  getUnscheduledDeliveries,
  getSupplierDeliverySummary,
  getDailyDeliveryReport,
  getOrdersReport,
  getSalesReport,
  getReportDates,
  getReportSummary,
  downloadReportExport,
  downloadExport,
  scheduleDelivery,
  createProduct,
  updateProduct,
  deleteProduct,
  getPriceLists,
  getPricePromotions,
  upsertPriceList,
  upsertPricePromotion,
  updatePricePromotion,
  deletePricePromotion,
  deletePriceList,
  createDistributionCentre,
  deleteDistributionCentre,
  deleteOrder,
  resetTestData,
};

export function getStatusLabel(status) {
  console.log("Order status raw:", status);

  const labels = {
    1: "Uploaded",
    2: "Validated",
    3: "Flagged",
    4: "Approved",
    5: "Processed",
  };

  const key = Number(status);
  return labels[key] ?? `Status ${status}`;
}

export function isFlaggedStatus(status) {
  return Number(status) === 3;
}

export function formatCurrency(value) {
  const amount = Number(value);
  if (Number.isNaN(amount)) {
    return "R 0.00";
  }

  return new Intl.NumberFormat("en-ZA", {
    style: "currency",
    currency: "ZAR",
    minimumFractionDigits: 2,
  }).format(amount);
}

export function formatAuditEntry(entry, order) {
  const orderLabel = order?.orderNumber
    ? order.orderNumber
    : entry?.entity === "Order"
      ? `Order ${entry.entityId}`
      : `${entry?.entity ?? "Record"} ${entry?.entityId ?? ""}`.trim();

  return `${orderLabel} ${entry?.field ?? "Field"} changed from ${entry?.oldValue ?? "-"} to ${entry?.newValue ?? "-"}`;
}

export function formatDateTime(value) {
  if (!value) {
    return "-";
  }

  const date = parseUtcDate(value);
  if (!date || Number.isNaN(date.getTime())) {
    return "-";
  }

  return new Intl.DateTimeFormat("en-ZA", {
    year: "numeric",
    month: "short",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  }).format(date);
}

export function formatRelativeTime(value) {
  if (!value) {
    return "-";
  }

  const timestamp = parseUtcDate(value)?.getTime();
  if (!timestamp || Number.isNaN(timestamp)) {
    return "-";
  }

  const diffMs = Date.now() - timestamp;
  const minute = 60 * 1000;
  const hour = 60 * minute;
  const day = 24 * hour;

  if (diffMs < minute) {
    return "Just now";
  }

  if (diffMs < hour) {
    const minutes = Math.max(1, Math.floor(diffMs / minute));
    return `${minutes} min ago`;
  }

  if (diffMs < day) {
    const hours = Math.max(1, Math.floor(diffMs / hour));
    return `${hours} hour${hours === 1 ? "" : "s"} ago`;
  }

  if (diffMs < day * 2) {
    return "Yesterday";
  }

  const days = Math.floor(diffMs / day);
  if (days < 7) {
    return `${days} days ago`;
  }

  return formatDate(value);
}
