import { formatDate, parseUtcDate } from "../utils/date.js";
export { formatDate, parseUtcDate };

const BASE_URL = "http://localhost:5070";
 

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
    const message =
      typeof payload === "object" && payload?.message
        ? payload.message
        : response.status >= 500
          ? "Server error. Please try again."
          : `Request failed (${response.status})`;

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
    const response = await fetch(`${BASE_URL}${path}`, requestOptions);
    return await handleResponse(response);
  } catch (error) {
    const method = (requestOptions.method ?? "GET").toUpperCase();
    const shouldRetry = method === "GET" && error instanceof TypeError;

    if (shouldRetry) {
      try {
        const retryResponse = await fetch(`${BASE_URL}${path}`, requestOptions);
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

export async function getUnscheduledOrders({ date } = {}) {
  return request(`/api/orders${toQueryString({ deliveryDate: date })}`, { method: "GET" });
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
        `${BASE_URL}/api/upload/csv${toQueryString({ allowDuplicates, createMissingProducts })}`,
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

export async function getProduction(date = getToday()) {
  const formattedDate = String(date);
  console.log("[API CALL] Date sent:", formattedDate);
  console.log("[API CALL] YYYY-MM-DD:", /^\d{4}-\d{2}-\d{2}$/.test(formattedDate));
  return request(`/api/production${toQueryString({ date })}`, { method: "GET" });
}

export async function getDeliveries(date = getToday()) {
  return request(`/api/delivery${toQueryString(normalizeDateFilter(date))}`, { method: "GET" });
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

export async function getReportSummary(date = getToday()) {
  return request(`/api/reports/summary${toQueryString({ date })}`, { method: "GET" });
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

export async function downloadReportExport(type, date = getToday()) {
  const response = await fetch(`${BASE_URL}/api/reports/export/${type}${toQueryString({ date })}`);

  if (!response.ok) {
    await handleResponse(response);
  }

  const csvText = await response.text();
  const blob = new Blob([csvText], { type: "text/csv;charset=utf-8" });
  const contentDisposition = response.headers.get("Content-Disposition");
  const fileName = getDownloadFileName(contentDisposition, `${type}-${date}.csv`);

  const url = window.URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  window.URL.revokeObjectURL(url);
}

export async function downloadExport(type, date = getToday()) {
  const response = await fetch(`${BASE_URL}/api/export/${type}${toQueryString({ date })}`);

  if (!response.ok) {
    await handleResponse(response);
  }

  const blob = await response.blob();
  const contentDisposition = response.headers.get("Content-Disposition");
  let fileName = "download.csv";

  if (contentDisposition && contentDisposition.includes("filename=")) {
    fileName = contentDisposition
      .split("filename=")[1]
      .replace(/"/g, "")
      .trim();
  }

  if (!fileName) {
    fileName = "download.csv";
  }

  const url = window.URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  window.URL.revokeObjectURL(url);
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

export async function getPriceLists() {
  return request("/api/admin/pricelists", { method: "GET" });
}

export async function upsertPriceList(payload) {
  return request("/api/admin/pricelists", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function createDistributionCentre(payload) {
  console.log("🌐 API FUNCTION CALLED with:", payload);
  return request("/api/admin/distributioncentres", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function resetTestData() {
  return request("/api/admin/reset-data", {
    method: "POST",
  });
}

export const api = {
  getOrders,
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
  getSupplierDeliverySummary,
  getDailyDeliveryReport,
  getOrdersReport,
  getSalesReport,
  getReportSummary,
  downloadReportExport,
  downloadExport,
  scheduleDelivery,
  createProduct,
  updateProduct,
  getPriceLists,
  upsertPriceList,
  createDistributionCentre,
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
