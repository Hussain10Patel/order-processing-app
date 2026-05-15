import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import ConfirmDeleteModal from "../components/ConfirmDeleteModal";
import DataTable from "../components/DataTable";
import DcLabel from "../components/DcLabel";
import OrdersFilters from "../components/OrdersFilters";
import StatusLabel from "../components/StatusLabel";
import StatusBlock from "../components/StatusBlock";
import {
  adjustOrder,
  createMissingDistributionCentres,
  createManualOrder,
  deleteOrder,
  formatCurrency,
  formatDate,
  getDistributionCentres,
  getOrderById,
  getOrders,
  getProducts,
  getSystemPrice,
  recalculateOrder,
  updateOrderStatus,
  isFlaggedStatus,
} from "../services/api";
import { rowMatchesSelectedDcs } from "../utils/distributionCentre";

const defaultManualForm = {
  orderNumber: "",
  orderDate: "",
  deliveryDate: "",
  distributionCentreId: "",
  items: [{ productId: "", quantity: "", price: "", systemPriceWarning: "", priceLookupPending: false }],
};

const statusOptions = [0, 1, 3, 4, 5, 8];

function formatDecimal(value) {
  const amount = Number(value);
  return Number.isFinite(amount) ? amount.toFixed(2) : "0.00";
}

function toSafeNumber(value, fallback = 0) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function getItemSku(item) {
  return String(item?.skuCode ?? item?.productCode ?? item?.productName ?? "UNKNOWN").trim() || "UNKNOWN";
}

function normalizeOrderItem(rawItem) {
  const rawQuantity = rawItem?.quantity;
  const rawUnitPrice = rawItem?.unitPrice ?? rawItem?.price;
  const rawLineTotal = rawItem?.lineTotal;

  console.log("[UI ITEM RAW]", {
    sku: getItemSku(rawItem),
    quantity: rawQuantity,
    unitPrice: rawUnitPrice,
    lineTotal: rawLineTotal,
  });

  const quantity = toSafeNumber(rawQuantity, 0);
  const unitPrice = toSafeNumber(rawUnitPrice, 0);
  const lineTotal = Number.isFinite(Number(rawLineTotal))
    ? Number(rawLineTotal)
    : quantity * unitPrice;

  const mappedItem = {
    ...rawItem,
    quantity,
    price: unitPrice,
    unitPrice,
    lineTotal,
  };

  console.log("[UI ITEM MAP]", {
    sku: getItemSku(mappedItem),
    quantity: mappedItem.quantity,
    unitPrice: mappedItem.unitPrice,
    lineTotal: mappedItem.lineTotal,
  });

  return mappedItem;
}

function normalizeOrder(order) {
  return {
    ...order,
    items: Array.isArray(order?.items) ? order.items.map(normalizeOrderItem) : [],
  };
}

function normalizeOrders(orders) {
  return (orders ?? []).map(normalizeOrder);
}

function calculateTotalQuantity(order) {
  return (order.items ?? []).reduce((sum, item) => sum + Number(item.quantity ?? 0), 0);
}

function hasPriceMismatch(item) {
  const expected = Number(item.quantity ?? 0) * Number(item.price ?? 0);
  const actual = Number(item.lineTotal ?? 0);
  return Math.abs(expected - actual) > 0.01;
}

function hasItemPricingIssue(item) {
  return Boolean(item?.isPriceMissing) || Boolean(item?.isPriceMismatch) || hasPriceMismatch(item);
}

function hasOrderPricingIssue(order) {
  return isFlaggedStatus(order?.status) || (order?.items ?? []).some((item) => Boolean(item?.isPriceMissing) || Boolean(item?.isPriceMismatch));
}

function normalizeText(value) {
  return String(value ?? "").trim().toLowerCase();
}

function hasProductionRequired(order) {
  if (Number(order?.productionRequired ?? 0) > 0) {
    return true;
  }

  return (order?.items ?? []).some((item) => Number(item?.productionRequired ?? 0) > 0);
}

function isApprovedStatus(order) {
  const normalizedStatus = String(order?.statusLabel ?? order?.status ?? "").trim().toLowerCase();
  return normalizedStatus === "approved";
}

function canApproveOrder(order) {
  const normalizedStatus = String(order?.statusLabel ?? order?.status ?? "").trim().toLowerCase();
  return normalizedStatus === "flagged" || normalizedStatus === "validated" || normalizedStatus === "pending";
}

function getDisplayProductTitle(item) {
  const productCode = String(item?.productCode ?? item?.skuCode ?? "").trim();
  const productName = String(item?.productName ?? "").trim();

  if (item?.isUnmapped) {
    if (productCode && productName) {
      return `${productCode} - ${productName}`;
    }

    if (productCode) {
      return productCode;
    }

    if (productName) {
      return productName;
    }
  }

  return productName || productCode || "Unnamed Product";
}

function getProductOptionLabel(product) {
  const productCode = String(product?.skuCode ?? "").trim();
  const productName = String(product?.name ?? "").trim();
  const prefix = productCode ? `${productCode} - ` : "";
  const suffix = (product?.isMapped === false || product?.requiresAttention) ? " (Unmapped)" : "";
  return `${prefix}${productName}${suffix}`.trim();
}

function OrdersPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const [orders, setOrders] = useState([]);
  const [products, setProducts] = useState([]);
  const [distributionCentres, setDistributionCentres] = useState([]);

  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  const [search, setSearch] = useState("");
  const [productCodeFilter, setProductCodeFilter] = useState("");
  const [productNameFilter, setProductNameFilter] = useState("");
  const [status, setStatus] = useState("All");
  const [selectedDistributionCentreIds, setSelectedDistributionCentreIds] = useState([]);
  const [fromDate, setFromDate] = useState("");
  const [toDate, setToDate] = useState("");
  const [currentPage, setCurrentPage] = useState(1);
  const [pageSize] = useState(20);
  const [totalCount, setTotalCount] = useState(0);

  const [sortKey, setSortKey] = useState("orderDate");
  const [sortDirection, setSortDirection] = useState("desc");
  const [selectedOrder, setSelectedOrder] = useState(null);
  const [editedItems, setEditedItems] = useState([]);
  const [hasDirtyDetailEdits, setHasDirtyDetailEdits] = useState(false);
  const [adjustmentNotes, setAdjustmentNotes] = useState("");
  const [adjusting, setAdjusting] = useState(false);
  const [adjustMessage, setAdjustMessage] = useState("");
  const [recalculating, setRecalculating] = useState(false);
  const [recalculateMessage, setRecalculateMessage] = useState("");

  const [manualForm, setManualForm] = useState(defaultManualForm);
  const [creating, setCreating] = useState(false);
  const [manualMessage, setManualMessage] = useState("");
  const [manualCentreFallback, setManualCentreFallback] = useState(null);
  const [refreshTick, setRefreshTick] = useState(0);
  const [lookupError, setLookupError] = useState("");
  const [highlightedOrderNumber, setHighlightedOrderNumber] = useState("");
  const [pageLoading, setPageLoading] = useState(false);
  const [refreshNotice, setRefreshNotice] = useState("");
  const [statusUpdatingId, setStatusUpdatingId] = useState(null);
  const [deleteDialog, setDeleteDialog] = useState(null);
  const [deletingOrder, setDeletingOrder] = useState(false);
  const detailRequestTokenRef = useRef(0);

  const productsById = useMemo(() => {
    return new Map(products.map((product) => [Number(product.id), product]));
  }, [products]);

  const distributionCentresById = useMemo(() => {
    return new Map(distributionCentres.map((centre) => [Number(centre.id), centre]));
  }, [distributionCentres]);

  const loadLookups = useCallback(async () => {
    try {
      setLookupError("");
      const [productsData, centresData] = await Promise.all([
        getProducts(),
        getDistributionCentres(),
      ]);

      const nextProducts = Array.isArray(productsData) ? productsData : [];
      const nextCentres = Array.isArray(centresData) ? centresData : [];

      setProducts(nextProducts);
      setDistributionCentres(nextCentres);
      console.log("DCs loaded:", nextCentres);

      return { productsData: nextProducts, centresData: nextCentres };
    } catch (requestError) {
      console.error("Failed to load order dropdowns:", requestError);
      setLookupError("Some dropdown data could not be loaded.");
      return { productsData: [], centresData: [] };
    }
  }, []);

  useEffect(() => {
    void loadLookups();
  }, [loadLookups]);

  useEffect(() => {
    async function handleOrdersRefresh() {
      setSelectedOrder(null);
      setEditedItems([]);
      setHasDirtyDetailEdits(false);
      setRefreshTick((current) => current + 1);

      await loadLookups();

      setRefreshNotice("Orders and lookups updated");
    }

    function handleLookupsRefresh() {
      void loadLookups();
    }

    window.addEventListener("orders:refresh", handleOrdersRefresh);
    window.addEventListener("lookups:refresh", handleLookupsRefresh);

    return () => {
      window.removeEventListener("orders:refresh", handleOrdersRefresh);
      window.removeEventListener("lookups:refresh", handleLookupsRefresh);
    };
  }, [loadLookups]);

  useEffect(() => {
    if (!refreshNotice) {
      return;
    }

    const timeoutId = window.setTimeout(() => {
      setRefreshNotice("");
    }, 2600);

    return () => window.clearTimeout(timeoutId);
  }, [refreshNotice]);

  useEffect(() => {
    let isDisposed = false;

    async function loadOrders() {
      setLoading(true);
      setError("");

      try {
        const filters = {
          ...(search.trim() && { orderNumber: search.trim() }),
          ...(productCodeFilter.trim() && { productCode: productCodeFilter.trim() }),
          ...(productNameFilter.trim() && { productName: productNameFilter.trim() }),
          ...(status && status !== "All" && { status }),
          ...(selectedDistributionCentreIds.length === 1 && {
            distributionCentreId: selectedDistributionCentreIds[0],
          }),
          ...(fromDate && { startDate: fromDate }),
          ...(toDate && { endDate: toDate }),
          page: currentPage,
          pageSize,
        };

        const ordersData = await getOrders(filters);
        console.log("GET /api/orders filters:", filters);
        console.log("GET /api/orders payload:", ordersData);

        if (isDisposed) {
          return;
        }

        const normalizedOrders = Array.isArray(ordersData)
          ? normalizeOrders(ordersData)
          : Array.isArray(ordersData?.items)
            ? normalizeOrders(ordersData.items)
            : [];
        const nextTotalCount = Number(ordersData?.totalCount);

        normalizedOrders.forEach((order) => {
          console.log("Order status:", order.status, order.statusLabel);
        });

        setOrders(normalizedOrders);
        setTotalCount(normalizedOrders.length);
      } catch (requestError) {
        if (isDisposed) {
          return;
        }

        console.error("Failed to load orders:", requestError);
        setOrders([]);
        setTotalCount(0);
        setError(requestError.message || "Unable to load orders");
      } finally {
        if (!isDisposed) {
          setLoading(false);
        }
      }
    }

    loadOrders();

    return () => {
      isDisposed = true;
    };
  }, [currentPage, fromDate, pageSize, productCodeFilter, productNameFilter, refreshTick, search, selectedDistributionCentreIds, status, toDate]);

  useEffect(() => {
    if (!pageLoading) {
      return;
    }

    const timeoutId = window.setTimeout(() => {
      setPageLoading(false);
    }, 180);

    return () => window.clearTimeout(timeoutId);
  }, [currentPage, pageLoading]);

  useEffect(() => {
    let isDisposed = false;

    async function loadManualPriceWarnings() {
      if (!manualForm.distributionCentreId) {
        setManualForm((current) => ({
          ...current,
          items: current.items.map((item) => ({
            ...item,
            systemPriceWarning: "",
            priceLookupPending: false,
          })),
        }));
        return;
      }

      const nextItems = await Promise.all(
        manualForm.items.map(async (item) => {
          if (!item.productId || !item.priceLookupPending) {
            return item;
          }

          try {
            await getSystemPrice(item.productId, manualForm.distributionCentreId);
            if (isDisposed) {
              return item;
            }

            return {
              ...item,
              systemPriceWarning: "",
              priceLookupPending: false,
            };
          } catch (lookupError) {
            if (isDisposed) {
              return item;
            }

            return {
              ...item,
              systemPriceWarning: "No system price configured (you can still enter price manually)",
              priceLookupPending: false,
            };
          }
        })
      );

      if (!isDisposed) {
        setManualForm((current) => ({
          ...current,
          items: current.items.map((item, index) => {
            const nextItem = nextItems[index];
            if (
              item.productId !== nextItem.productId ||
              String(current.distributionCentreId) !== String(manualForm.distributionCentreId)
            ) {
              return item;
            }

            return nextItem;
          }),
        }));
      }
    }

    const needsPriceCheck = manualForm.items.some((item) => item.priceLookupPending);
    if (!needsPriceCheck) {
      return undefined;
    }

    loadManualPriceWarnings();

    return () => {
      isDisposed = true;
    };
  }, [manualForm.distributionCentreId, manualForm.items]);

  useEffect(() => {
    if (!selectedOrder) {
      return;
    }

    const nextSelectedOrder = orders.find((order) => order.id === selectedOrder.id) ?? null;

    if (!nextSelectedOrder) {
      setSelectedOrder(null);
      setEditedItems([]);
      setHasDirtyDetailEdits(false);
      return;
    }

    setSelectedOrder(nextSelectedOrder);

    if (!hasDirtyDetailEdits) {
      const normalizedOrder = normalizeOrder(nextSelectedOrder);
      setEditedItems(normalizedOrder.items ?? []);
    }
  }, [orders, selectedOrder, hasDirtyDetailEdits]);

  useEffect(() => {
    editedItems.forEach((item) => {
      console.log("[UI ITEM DISPLAY]", {
        sku: getItemSku(item),
        quantity: toSafeNumber(item?.quantity, 0),
        unitPrice: toSafeNumber(item?.unitPrice ?? item?.price, 0),
        lineTotal: toSafeNumber(item?.lineTotal, 0),
      });
    });
  }, [editedItems]);

  useEffect(() => {
    const focusOrderNumber = location.state?.focusOrderNumber?.trim();
    const focusToken = location.state?.focusToken;

    if (!focusOrderNumber || !focusToken) {
      return;
    }

    setSearch(focusOrderNumber);
    setStatus("All");
    setSelectedDistributionCentreIds([]);
    setFromDate("");
    setToDate("");
    setCurrentPage(1);
    setHighlightedOrderNumber(focusOrderNumber);

    navigate(location.pathname, { replace: true, state: null });

    const timeoutId = window.setTimeout(() => {
      setHighlightedOrderNumber("");
    }, 3500);

    return () => window.clearTimeout(timeoutId);
  }, [location.pathname, location.state, navigate]);

  useEffect(() => {
    if (!highlightedOrderNumber) {
      return;
    }

    const matchedOrder = orders.find(
      (order) => String(order.orderNumber).toLowerCase() === highlightedOrderNumber.toLowerCase()
    );

    if (matchedOrder) {
      const normalizedMatchedOrder = normalizeOrder(matchedOrder);
      setSelectedOrder(normalizedMatchedOrder);
      setEditedItems(normalizedMatchedOrder.items ?? []);
      setHasDirtyDetailEdits(false);
    }
  }, [highlightedOrderNumber, orders]);

  async function openOrderDetails(order) {
    if (!order?.id) {
      return;
    }

    setAdjustMessage("");
    setRecalculateMessage("");
    setAdjustmentNotes("");
    setHasDirtyDetailEdits(false);

    const normalizedOrder = normalizeOrder(order);
    setSelectedOrder(normalizedOrder);
    setEditedItems(normalizedOrder.items ?? []);

    const requestToken = detailRequestTokenRef.current + 1;
    detailRequestTokenRef.current = requestToken;

    try {
      const latestOrder = await getOrderById(order.id);

      if (detailRequestTokenRef.current !== requestToken || !latestOrder) {
        return;
      }

      const normalizedLatestOrder = normalizeOrder(latestOrder);
      setSelectedOrder(normalizedLatestOrder);
      setEditedItems(normalizedLatestOrder.items ?? []);
      setHasDirtyDetailEdits(false);
    } catch (requestError) {
      if (detailRequestTokenRef.current !== requestToken) {
        return;
      }

      console.error("Failed loading latest order detail:", requestError);
      setError(requestError.message || "Unable to refresh order detail");
    }
  }

  function closeOrderDetails() {
    setSelectedOrder(null);
    setEditedItems([]);
    setHasDirtyDetailEdits(false);
    setAdjustMessage("");
    setRecalculateMessage("");
    setAdjustmentNotes("");
  }

  async function handleUpdateOrderStatus(order, newStatus) {
    if (!order?.id) {
      return;
    }

    setStatusUpdatingId(order.id);
    console.log("Updating order status:", order.id, newStatus);

    try {
      await updateOrderStatus(order.id, newStatus);
      setError("");
      setRefreshTick((current) => current + 1);

      if (String(newStatus).trim().toLowerCase() === "processed") {
        window.dispatchEvent(new Event("orders:refresh"));
      }
    } catch (requestError) {
      console.error("Failed updating order status:", requestError);

      if (String(newStatus).trim().toLowerCase() === "processed") {
        setError("Insufficient stock to process this order");
      } else {
        setError(requestError.message || "Failed updating order status");
      }
    } finally {
      setStatusUpdatingId(null);
    }
  }

  function handleMapProduct(row) {
    const product = getOrderItemProduct(row);
    navigate("/admin", {
      state: {
        mapProduct: {
          id: product?.id ?? row.productId,
          skuCode: row.productCode || row.skuCode || "",
          name: row.productName || product?.name || "",
          palletConversionRate: product?.palletConversionRate ? String(product.palletConversionRate) : "1",
        },
      },
    });
  }

  function getOrderItemProduct(item) {
    return productsById.get(Number(item?.productId));
  }

  function isUnmappedProduct(item) {
    const product = getOrderItemProduct(item);
    return product?.isMapped === false || Boolean(product?.requiresAttention) || (!product && (Boolean(item?.isUnmapped) || normalizeText(item?.productName).startsWith("unmapped product")));
  }

  function hasRecentlyMappedProduct(item) {
    const product = getOrderItemProduct(item);
    return Boolean(item?.isUnmapped) && Boolean(product) && product?.isMapped !== false && !product.requiresAttention;
  }

  function usesDefaultPalletConversion(item) {
    const product = getOrderItemProduct(item);
    return Boolean(product?.requiresAttention) || Number(product?.palletConversionRate ?? 0) <= 0;
  }

  function canRecalculateOrder(order) {
    return (order?.items ?? []).some((item) => isUnmappedProduct(item) || hasRecentlyMappedProduct(item));
  }

  const filteredOrders = useMemo(() => {
    return orders.filter((order) => rowMatchesSelectedDcs(order, selectedDistributionCentreIds));
  }, [orders, selectedDistributionCentreIds]);

  const sortedOrders = useMemo(() => {
    return [...filteredOrders].sort((a, b) => {
      const direction = sortDirection === "asc" ? 1 : -1;
      const aValue = sortKey === "totalQuantity" ? calculateTotalQuantity(a) : a[sortKey];
      const bValue = sortKey === "totalQuantity" ? calculateTotalQuantity(b) : b[sortKey];

      if (String(sortKey).toLowerCase().includes("date")) {
        return (new Date(aValue).getTime() - new Date(bValue).getTime()) * direction;
      }

      if (typeof aValue === "number" && typeof bValue === "number") {
        return (aValue - bValue) * direction;
      }

      return String(aValue ?? "").localeCompare(String(bValue ?? "")) * direction;
    });
  }, [filteredOrders, sortKey, sortDirection]);

  const paginatedOrders = useMemo(() => {
    const startIndex = (currentPage - 1) * pageSize;
    return sortedOrders.slice(startIndex, startIndex + pageSize);
  }, [currentPage, pageSize, sortedOrders]);

  const totalPages = useMemo(() => {
    return Math.max(1, Math.ceil(filteredOrders.length / pageSize));
  }, [filteredOrders.length, pageSize]);

  useEffect(() => {
    if (currentPage <= totalPages) {
      return;
    }

    setCurrentPage(totalPages);
  }, [currentPage, totalPages]);

  const columns = useMemo(
    () => [
      { key: "orderNumber", header: "Order Number", sortable: true },
      {
        key: "orderDate",
        header: "Order Date",
        sortable: true,
        render: (row) => formatDate(row.orderDate),
      },
      {
        key: "deliveryDate",
        header: "Delivery Date",
        sortable: true,
        render: (row) => formatDate(row.deliveryDate),
      },
      {
        key: "distributionCentreName",
        header: "Distribution Centre",
        sortable: true,
        render: (row) => <DcLabel row={row} />,
      },
      {
        key: "totalQuantity",
        header: "Quantity",
        sortable: true,
        render: (row) => <span className="metric-cell">{formatDecimal(calculateTotalQuantity(row))}</span>,
      },
      {
        key: "status",
        header: "Status",
        sortable: true,
        render: (row) => {
          console.log("Order row:", row.orderNumber, row.isPriceMissing, row.isPriceMismatch);

          return (
            <div className="status-stack">
              <StatusLabel status={row.status} label={row.statusLabel} />
              {row.isPriceMissing ? (
                <span className="badge orange" style={{ marginLeft: 8 }}>
                  No Price Configured
                </span>
              ) : row.isPriceMismatch ? (
                <span className="badge yellow" style={{ marginLeft: 8 }}>
                  ⚠ Price Mismatch
                </span>
              ) : null}
            </div>
          );
        },
      },
      {
        key: "totalPallets",
        header: "Pallets",
        sortable: true,
        render: (row) => <span className="metric-cell">{formatDecimal(row.totalPallets)}</span>,
      },
      {
        key: "totalValue",
        header: "Total Value",
        sortable: true,
        render: (row) => formatCurrency(row.totalValue),
      },
      {
        key: "actions",
        header: "Actions",
        render: (row) => {
          const showApprove = canApproveOrder(row);
          const showProcess = isApprovedStatus(row);
          const disableProcessAction =
            statusUpdatingId === row.id ||
            hasProductionRequired(row) ||
            !isApprovedStatus(row);
          const disableApproveAction = statusUpdatingId === row.id;

          return (
            <div className="action-row">
              <button
                type="button"
                className="secondary table-action-button"
                onClick={(event) => {
                  event.stopPropagation();
                  openOrderDetails(row);
                }}
              >
                View
              </button>
              {showApprove && (
                <button
                  type="button"
                  className="secondary table-action-button"
                  disabled={disableApproveAction}
                  onClick={(event) => {
                    event.stopPropagation();
                    void handleUpdateOrderStatus(row, "Approved");
                  }}
                >
                  Approve
                </button>
              )}
              {showProcess && (
                <button
                  type="button"
                  className="secondary table-action-button"
                  disabled={disableProcessAction}
                  onClick={(event) => {
                    event.stopPropagation();
                    void handleUpdateOrderStatus(row, "Processed");
                  }}
                >
                  Process
                </button>
              )}
              <button
                type="button"
                className="danger table-action-button"
                onClick={(event) => {
                  event.stopPropagation();
                  openDeleteDialog(row);
                }}
              >
                Delete
              </button>
            </div>
          );
        },
      },
    ],
    [statusUpdatingId]
  );

  const itemColumns = useMemo(
    () => [
      {
        key: "productName",
        header: "Product",
        render: (row) => {
          const productCode = row.productCode || row.skuCode || "-";
          const productName = row.productName || "-";

          return (
          <div className="item-product-cell">
            <div className="item-product-heading">
              <div className="item-product-title-row">
                <strong>{productCode}</strong>
                <span>{productName !== "-" ? `- ${productName}` : ""}</span>
              </div>
              {isUnmappedProduct(row) && (
                <span
                  className="status-chip warning"
                  title="This product was found in the uploaded CSV but is not yet mapped in the system."
                >
                  Unmapped
                </span>
              )}
            </div>
            <div className="product-meta-grid">
              <span><strong>Product Code:</strong> {productCode}</span>
              <span><strong>Product Name:</strong> {productName}</span>
            </div>
            <div className="item-warning-stack">
              {isUnmappedProduct(row) && (
                <span className="status-chip warning">This product is not yet configured. CSV data is being used.</span>
              )}
              {hasRecentlyMappedProduct(row) && (
                <span className="status-chip success">Product mapping was updated. Recalculate to refresh pallets and totals.</span>
              )}
              {row.isPriceMissing && (
                <span className="status-chip warning">Using CSV price (no system price configured)</span>
              )}
              {row.isPriceMismatch && (
                <span className="status-chip warning">⚠ Price differs from system price</span>
              )}
              {usesDefaultPalletConversion(row) && (
                <span className="status-chip warning">⚠ Default pallet conversion used</span>
              )}
            </div>
            {isUnmappedProduct(row) && (
              <div>
                <button
                  type="button"
                  className="secondary table-action-button"
                  onClick={(event) => {
                    event.stopPropagation();
                    handleMapProduct(row);
                  }}
                >
                  Map Product
                </button>
              </div>
            )}
          </div>
        );
        },
      },
      {
        key: "quantity",
        header: "Quantity",
        render: (row, index) => (
          <input
            className={hasItemPricingIssue(row) ? "field-alert" : ""}
            type="number"
            min="0.01"
            step="0.01"
            value={row.quantity ?? ""}
            onChange={(event) => updateSelectedOrderItem(index, "quantity", event.target.value)}
          />
        ),
      },
      {
        key: "price",
        header: "Price",
        render: (row, index) => (
          <input
            className={hasItemPricingIssue(row) ? "field-alert" : ""}
            type="number"
            min="0.01"
            step="0.01"
            value={row.unitPrice ?? row.price ?? ""}
            onChange={(event) => {
              const rawValue = event.target.value;
              const normalizedValue = rawValue.replace(/,/g, ".");
              updateSelectedOrderItem(index, "price", normalizedValue);
            }}
          />
        ),
      },
      { key: "pallets", header: "Pallets", render: (row) => formatDecimal(row.pallets) },
      {
        key: "lineTotal",
        header: "Line Total",
        render: (row) => (
          <div className={hasItemPricingIssue(row) ? "mismatch-cell" : undefined}>
            <span>{formatCurrency(row.lineTotal)}</span>
            {hasItemPricingIssue(row) && <span className="mini-flag">⚠ Price Mismatch</span>}
          </div>
        ),
      },
    ],
    [navigate, productsById, selectedOrder]
  );

  function handleSort(nextKey) {
    if (nextKey === sortKey) {
      setSortDirection((current) => (current === "asc" ? "desc" : "asc"));
      return;
    }

    setSortKey(nextKey);
    setSortDirection("asc");
  }

  function clearFilters() {
    const isAlreadyDefault =
      search === "" &&
      productCodeFilter === "" &&
      productNameFilter === "" &&
      status === "All" &&
      selectedDistributionCentreIds.length === 0 &&
      fromDate === "" &&
      toDate === "" &&
      currentPage === 1;

    setSearch("");
    setProductCodeFilter("");
    setProductNameFilter("");
    setStatus("All");
    setSelectedDistributionCentreIds([]);
    setFromDate("");
    setToDate("");
    setCurrentPage(1);

    if (isAlreadyDefault) {
      setRefreshTick((current) => current + 1);
    }
  }

  function handleSearchChange(value) {
    setSearch(value);
    setCurrentPage(1);
  }

  function handleStatusChange(value) {
    setStatus(value);
    setCurrentPage(1);
  }

  function handleProductCodeChange(value) {
    setProductCodeFilter(value);
    setCurrentPage(1);
  }

  function handleProductNameChange(value) {
    setProductNameFilter(value);
    setCurrentPage(1);
  }

  function handleDistributionCentreChange(values) {
    setSelectedDistributionCentreIds(values);
    setCurrentPage(1);
  }

  function handleFromDateChange(value) {
    setFromDate(value);
    setCurrentPage(1);
  }

  function handleToDateChange(value) {
    setToDate(value);
    setCurrentPage(1);
  }

  function changePage(nextPage) {
    if (nextPage < 1 || nextPage > totalPages || nextPage === currentPage) {
      return;
    }

    setPageLoading(true);
    setCurrentPage(nextPage);
  }

  function updateManualField(field, value) {
    setManualForm((current) => ({ ...current, [field]: value }));
  }

  function getSelectedDistributionCentre() {
    const selectedId = Number(manualForm.distributionCentreId);
    if (!Number.isInteger(selectedId) || selectedId <= 0) {
      return null;
    }

    return distributionCentresById.get(selectedId) ?? null;
  }

  function updateManualItem(index, field, value) {
    setManualForm((current) => {
      const items = [...current.items];
      const nextItem = { ...items[index], [field]: value };

      if (field === "productId") {
        nextItem.systemPriceWarning = "";
        nextItem.priceLookupPending = false;
      }

      items[index] = nextItem;
      return { ...current, items };
    });
  }

  function addManualItem() {
    setManualForm((current) => ({
      ...current,
      items: [
        ...current.items,
        { productId: "", quantity: "", price: "", systemPriceWarning: "", priceLookupPending: false },
      ],
    }));
  }

  function removeManualItem(index) {
    setManualForm((current) => ({
      ...current,
      items: current.items.filter((_, itemIndex) => itemIndex !== index),
    }));
  }

  function updateSelectedOrderItem(index, field, value) {
    const normalizedValue = String(value ?? "").replace(/,/g, ".");
    const parsedValue = parseFloat(normalizedValue);
    const safeValue = Number.isFinite(parsedValue) ? parsedValue : 0;
    setHasDirtyDetailEdits(true);

    setEditedItems((current) =>
      current.map((item, itemIndex) => {
        if (itemIndex !== index) {
          return item;
        }

        const nextItem = { ...item };

        if (field === "quantity") {
          nextItem.quantity = safeValue;
        }

        if (field === "price") {
          nextItem.price = safeValue;
          nextItem.unitPrice = safeValue;
        }

        const quantity = Number(field === "quantity" ? safeValue : nextItem.quantity);
        const price = Number(field === "price" ? safeValue : (nextItem.unitPrice ?? nextItem.price));
        nextItem.lineTotal = quantity * price;
        return nextItem;
      })
    );
  }

  function validateManualForm() {
    if (!manualForm.orderNumber || !manualForm.orderDate || !manualForm.deliveryDate) {
      return "Order number, order date, and delivery date are required.";
    }

    if (!manualForm.distributionCentreId) {
      return "Distribution centre is required.";
    }

    if (!manualForm.items.length) {
      return "At least one item is required.";
    }

    const hasInvalidItem = manualForm.items.some((item) => {
      return (
        !item.productId ||
        Number(item.quantity) <= 0 ||
        Number.isNaN(Number(item.quantity)) ||
        Number(item.price) <= 0 ||
        Number.isNaN(Number(item.price))
      );
    });

    if (hasInvalidItem) {
      return "Each item must have product, quantity > 0, and price > 0.";
    }

    return "";
  }

  async function submitManualOrder(event) {
    event.preventDefault();
    setCreating(true);
    setManualMessage("");
    setManualCentreFallback(null);

    const validationError = validateManualForm();
    if (validationError) {
      setManualMessage(validationError);
      setCreating(false);
      return;
    }

    const selectedDistributionCentre = getSelectedDistributionCentre();
    console.log("Selected DC:", selectedDistributionCentre);

    if (!selectedDistributionCentre) {
      setManualMessage("Selected distribution centre could not be matched. Please contact admin or reselect.");
      setCreating(false);
      return;
    }

    try {
      const payload = {
        orderNumber: manualForm.orderNumber.trim(),
        orderDate: manualForm.orderDate,
        deliveryDate: manualForm.deliveryDate,
        distributionCentreId: selectedDistributionCentre.id,
        items: manualForm.items.map((item) => ({
          productId: Number(item.productId),
          quantity: Number(item.quantity),
          price: Number(item.price),
        })),
      };

      await createManualOrder(payload);
      setManualMessage("Manual order created successfully");
      setManualForm(defaultManualForm);
      setRefreshTick((current) => current + 1);
    } catch (createError) {
      console.error("Failed creating manual order:", createError);
      const nextMessage = createError.message || "Failed creating manual order";
      if (nextMessage.includes("Selected distribution centre could not be matched")) {
        setManualCentreFallback(selectedDistributionCentre);
      }
      setManualMessage(nextMessage);
    } finally {
      setCreating(false);
    }
  }

  async function handleCreateManualDistributionCentre() {
    if (!manualCentreFallback?.name) {
      setManualCentreFallback(null);
      return;
    }

    setCreating(true);
    setManualMessage("");

    try {
      await createMissingDistributionCentres([manualCentreFallback.name]);
      const { centresData: nextCentres } = await loadLookups();

      const matchedCentre = nextCentres.find((centre) => centre.name === manualCentreFallback.name);
      if (matchedCentre) {
        setManualForm((current) => ({
          ...current,
          distributionCentreId: String(matchedCentre.id),
        }));
      }

      setManualMessage("Distribution centre created successfully. Please review and submit the order again.");
      setManualCentreFallback(null);
    } catch (fallbackError) {
      setManualMessage(fallbackError.message || "Failed creating distribution centre");
    } finally {
      setCreating(false);
    }
  }

  async function submitAdjustment() {
    if (!selectedOrder) {
      return;
    }

    setAdjusting(true);
    setAdjustMessage("");

    try {
      console.log("Submitting items:", editedItems);

      const payload = {
        items: editedItems.map((item) => ({
          productId: item.productId,
          quantity: Number(String(item.quantity).replace(/,/g, ".")),
          price: Number(String(item.price).replace(/,/g, ".")),
        })),
        notes: adjustmentNotes || null,
      };

      const updatedOrder = await adjustOrder(selectedOrder.id, payload);

      if (updatedOrder) {
        const normalizedUpdatedOrder = normalizeOrder(updatedOrder);
        setSelectedOrder(normalizedUpdatedOrder);
        setEditedItems(normalizedUpdatedOrder.items ?? []);
        setHasDirtyDetailEdits(false);
        setOrders((current) => current.map((order) => (order.id === normalizedUpdatedOrder.id ? normalizedUpdatedOrder : order)));
      }

      setAdjustMessage("Order adjusted successfully");
      setAdjustmentNotes("");
      setRefreshTick((current) => current + 1);
    } catch (adjustError) {
      console.error("Failed adjusting order:", adjustError);
      setAdjustMessage(String(adjustError.message || "Failed to adjust order"));
    } finally {
      setAdjusting(false);
    }
  }

  async function handleRecalculateOrder() {
    if (!selectedOrder) {
      return;
    }

    setRecalculating(true);
    setRecalculateMessage("");

    try {
      const refreshedOrder = await recalculateOrder(selectedOrder.id);
      if (refreshedOrder) {
        const normalizedRefreshedOrder = normalizeOrder(refreshedOrder);
        setSelectedOrder(normalizedRefreshedOrder);
        setEditedItems(normalizedRefreshedOrder.items ?? []);
        setHasDirtyDetailEdits(false);
      }

      setRefreshTick((current) => current + 1);
      setRecalculateMessage("Order recalculated using latest product mappings");
    } catch (requestError) {
      console.error("Failed recalculating order:", requestError);
      setRecalculateMessage(requestError.message || "Failed to recalculate order");
    } finally {
      setRecalculating(false);
    }
  }

  function openDeleteDialog(order) {
    setDeleteDialog(order);
  }

  function closeDeleteDialog() {
    if (deletingOrder) {
      return;
    }

    setDeleteDialog(null);
  }

  async function confirmDeleteOrder() {
    if (!deleteDialog?.id) {
      return;
    }

    setDeletingOrder(true);

    try {
      await deleteOrder(deleteDialog.id);

      setOrders((current) => current.filter((order) => order.id !== deleteDialog.id));
      setTotalCount((current) => Math.max(0, current - 1));

      if (selectedOrder?.id === deleteDialog.id) {
        setSelectedOrder(null);
        setEditedItems([]);
        setHasDirtyDetailEdits(false);
      }

      setError("");
      setRefreshNotice("Order deleted successfully");
    } catch (requestError) {
      setError(requestError.message || "Failed deleting order");
    } finally {
      setDeletingOrder(false);
      setDeleteDialog(null);
    }
  }

  return (
    <section>
      <header className="page-header">
        <h2>Orders Dashboard</h2>
        <p>CSV to order verification, adjustments, and delivery readiness.</p>
        {refreshNotice && <p className="status-text order-refresh-notice">{refreshNotice}</p>}
      </header>

      <div className="panel">
        <div className="section-heading">
          <h3>Orders</h3>
        </div>

        <OrdersFilters
          search={search}
          onSearch={handleSearchChange}
          productCode={productCodeFilter}
          onProductCode={handleProductCodeChange}
          productName={productNameFilter}
          onProductName={handleProductNameChange}
          status={status}
          onStatus={handleStatusChange}
          statuses={statusOptions}
          selectedDistributionCentreIds={selectedDistributionCentreIds}
          onSelectedDistributionCentreIds={handleDistributionCentreChange}
          distributionCentres={distributionCentres}
          fromDate={fromDate}
          onFromDate={handleFromDateChange}
          toDate={toDate}
          onToDate={handleToDateChange}
          onClear={clearFilters}
        />

        {lookupError && <p className="alert error">{lookupError}</p>}

        <StatusBlock
          loading={loading}
          error={error}
          empty={!loading && !error && filteredOrders.length === 0}
          loadingText="Loading orders..."
          emptyText="No orders found"
          spinner
        />

        {!loading && !error && filteredOrders.length > 0 && (
          <div className={`orders-table-region${pageLoading ? " is-loading" : ""}`}>
            <DataTable
              columns={columns}
              data={paginatedOrders}
              rowKey="id"
              onRowClick={openOrderDetails}
              rowClassName={(row) => {
                const classes = [];

                if (isFlaggedStatus(row.status)) {
                  classes.push("row-flagged");
                }

                if (
                  highlightedOrderNumber &&
                  String(row.orderNumber).toLowerCase() === highlightedOrderNumber.toLowerCase()
                ) {
                  classes.push("row-highlight");
                }

                return classes.join(" ");
              }}
              sortKey={sortKey}
              sortDirection={sortDirection}
              onSort={handleSort}
            />

            <div className="pagination-bar">
              <span className="pagination-summary">
                {pageLoading ? "Loading page..." : `${filteredOrders.length} filtered orders`}
              </span>
              <div className="pagination-controls">
                <button type="button" className="secondary" onClick={() => changePage(currentPage - 1)} disabled={currentPage === 1 || loading || pageLoading}>
                  Previous
                </button>
                <span className="pagination-indicator">Page {currentPage} of {totalPages}</span>
                <button
                  type="button"
                  className="secondary"
                  onClick={() => changePage(currentPage + 1)}
                  disabled={currentPage >= totalPages || loading || pageLoading || paginatedOrders.length < pageSize && currentPage >= totalPages}
                >
                  Next
                </button>
              </div>
            </div>
          </div>
        )}
      </div>

      {selectedOrder && (
        <div className="panel order-detail-panel">
          <div className="section-heading">
            <h3>Order Details: {selectedOrder.orderNumber}</h3>
            <div className="action-row">
              {canRecalculateOrder(selectedOrder) && (
                <button
                  type="button"
                  className="secondary"
                  title="Recalculate pallets and totals after product mapping"
                  onClick={handleRecalculateOrder}
                  disabled={recalculating}
                >
                  {recalculating ? "Recalculating..." : "Recalculate Order"}
                </button>
              )}
              <button type="button" className="secondary" onClick={closeOrderDetails}>
                Close
              </button>
            </div>
          </div>
          <div className="detail-strip">
            <StatusLabel status={selectedOrder.status} label={selectedOrder.statusLabel} />
            {selectedOrder.isPriceMissing && (
              <span className="status-chip" style={{backgroundColor: "#ff9800"}}>No Price Configured</span>
            )}
            {selectedOrder.isPriceMismatch && (
              <span className="status-chip warning">⚠ Price Mismatch</span>
            )}
            <span className="status-chip">Pallets: {formatDecimal(selectedOrder.totalPallets)}</span>
            <span className="status-chip">Total Value: {formatCurrency(selectedOrder.totalValue)}</span>
            {selectedOrder.isAdjusted && <span className="status-chip warning">Adjusted</span>}
          </div>

          <div className="order-info-grid">
            <div className="order-info-card">
              <span>Order Number</span>
              <strong>{selectedOrder.orderNumber}</strong>
            </div>
            <div className="order-info-card">
              <span>Order Date</span>
              <strong>{formatDate(selectedOrder.orderDate)}</strong>
            </div>
            <div className="order-info-card">
              <span>Delivery Date</span>
              <strong>{formatDate(selectedOrder.deliveryDate)}</strong>
            </div>
            <div className="order-info-card">
              <span>Distribution Centre</span>
              <strong><DcLabel row={selectedOrder} /></strong>
            </div>
          </div>

          <p className="status-text">Notes: {selectedOrder.notes || "-"}</p>

          <div className="section-heading order-detail-subheading">
            <h4>Line Items</h4>
          </div>

          <DataTable
            columns={itemColumns}
            data={editedItems}
            rowKey="id"
            rowClassName={(row) => (hasItemPricingIssue(row) ? "row-flagged" : "")}
            sortKey=""
            sortDirection="asc"
            onSort={() => {}}
          />

          <div style={{ marginTop: 12 }}>
            <label>Adjustment Notes</label>
            <textarea value={adjustmentNotes} onChange={(event) => setAdjustmentNotes(event.target.value)} />
          </div>
          <div style={{ marginTop: 12 }}>
            <button type="button" onClick={submitAdjustment} disabled={adjusting}>
              {adjusting ? "Adjusting..." : "Adjust Order"}
            </button>
          </div>
          {adjustMessage && (
            <p className={adjustMessage.includes("success") ? "alert success" : "alert error"}>
              {adjustMessage}
            </p>
          )}
          {recalculateMessage && (
            <p className={recalculateMessage.includes("latest product mappings") ? "alert success" : "alert error"}>
              {recalculateMessage}
            </p>
          )}
        </div>
      )}

      <div className="panel">
        <h3>Manual Order Creation</h3>
        <form onSubmit={submitManualOrder}>
          <div className="grid-2">
            <div>
              <label>Order Number</label>
              <input
                required
                value={manualForm.orderNumber}
                onChange={(event) => updateManualField("orderNumber", event.target.value)}
              />
            </div>

            <div>
              <label>Distribution Centre</label>
              <select
                required
                value={manualForm.distributionCentreId}
                onChange={(event) => {
                  const nextDistributionCentreId = event.target.value;
                  const selectedCentre = distributionCentres.find(
                    (centre) => String(centre.id) === String(nextDistributionCentreId)
                  ) ?? null;
                  console.log("Selected DC:", selectedCentre);
                  setManualForm((current) => ({
                    ...current,
                    distributionCentreId: nextDistributionCentreId,
                    items: current.items.map((item) => ({
                      ...item,
                      systemPriceWarning: "",
                      priceLookupPending: Boolean(item.productId && nextDistributionCentreId),
                    })),
                  }));
                  setManualCentreFallback(null);
                }}
              >
                <option value="">Select Distribution Centre</option>
                {distributionCentres.map((dc) => (
                  <option key={dc.id} value={dc.id}>
                    {dc.name}
                  </option>
                ))}
              </select>
            </div>

            <div>
              <label>Order Date</label>
              <input
                required
                type="date"
                value={manualForm.orderDate}
                onChange={(event) => updateManualField("orderDate", event.target.value)}
              />
            </div>

            <div>
              <label>Delivery Date</label>
              <input
                required
                type="date"
                value={manualForm.deliveryDate}
                onChange={(event) => updateManualField("deliveryDate", event.target.value)}
              />
            </div>
          </div>

          <h4>Items</h4>
          {manualForm.items.map((item, index) => (
            <div className="grid-2" key={`manual-item-${index}`}>
              <div>
                <label>Product</label>
                <select
                  required
                  value={item.productId}
                  onChange={(event) => {
                    const nextProductId = event.target.value;
                    const product = productsById.get(Number(nextProductId));

                    setManualForm((current) => {
                      const items = [...current.items];
                      items[index] = {
                        ...items[index],
                        productId: nextProductId,
                        systemPriceWarning: "",
                        priceLookupPending: Boolean(nextProductId && current.distributionCentreId),
                      };

                      return { ...current, items };
                    });

                  }}
                >
                  <option value="">Select Product</option>
                  {products.map((product) => (
                    <option key={product.id} value={product.id}>
                      {getProductOptionLabel(product)}
                    </option>
                  ))}
                </select>
              </div>

              <div>
                <label>Quantity</label>
                <input
                  required
                  type="number"
                  min="0.01"
                  step="0.01"
                  value={item.quantity}
                  onChange={(event) => updateManualItem(index, "quantity", event.target.value)}
                />
              </div>

              <div>
                <label>Price</label>
                <input
                  required
                  type="number"
                  min="0.01"
                  step="0.01"
                  value={item.price}
                  onChange={(event) => updateManualItem(index, "price", event.target.value)}
                  placeholder="Enter unit price"
                />
                {item.systemPriceWarning && <p className="status-text warning-text form-note">⚠ You can enter price manually</p>}
                {productsById.get(Number(item.productId))?.requiresAttention && (
                  <p className="status-text warning-text form-note">This product is not yet configured. You can still proceed.</p>
                )}
                {Number(productsById.get(Number(item.productId))?.palletConversionRate ?? 0) <= 0 && item.productId && (
                  <p className="status-text warning-text form-note">⚠ Default pallet conversion used</p>
                )}
              </div>

              <div>
                <label>Calculated Total</label>
                <input
                  readOnly
                  value={item.price ? formatCurrency(Number(item.quantity || 0) * Number(item.price || 0)) : ""}
                  placeholder="Calculated automatically"
                />
              </div>

              <div style={{ display: "flex", alignItems: "end" }}>
                <button
                  type="button"
                  className="secondary"
                  onClick={() => removeManualItem(index)}
                  disabled={manualForm.items.length === 1 || creating}
                >
                  Remove Item
                </button>
              </div>
            </div>
          ))}

          <div style={{ marginTop: 12, display: "flex", gap: 8 }}>
            <button type="button" className="secondary" onClick={addManualItem} disabled={creating}>
              Add Item
            </button>
            <button type="submit" disabled={creating}>
              {creating ? "Saving..." : "Create Manual Order"}
            </button>
          </div>
        </form>

        {manualCentreFallback && (
          <div className="manual-fallback-panel">
            <p className="alert warning">
              Selected distribution centre could not be matched. Create this distribution centre?
            </p>
            <div className="action-row">
              <button type="button" onClick={handleCreateManualDistributionCentre} disabled={creating}>
                Yes
              </button>
              <button type="button" className="secondary" onClick={() => setManualCentreFallback(null)} disabled={creating}>
                No
              </button>
            </div>
          </div>
        )}

        {manualMessage && (
          <p className={manualMessage.includes("success") ? "alert success" : "alert error"}>
            {manualMessage}
          </p>
        )}
      </div>

      <ConfirmDeleteModal
        open={Boolean(deleteDialog)}
        title="Delete Order"
        message={`Are you sure you want to delete this? (${deleteDialog?.orderNumber ?? ""})`}
        confirmText="Delete"
        confirming={deletingOrder}
        onCancel={closeDeleteDialog}
        onConfirm={confirmDeleteOrder}
      />
    </section>
  );
}

export default OrdersPage;
