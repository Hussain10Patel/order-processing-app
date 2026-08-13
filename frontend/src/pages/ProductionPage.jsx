import { useEffect, useMemo, useState } from "react";
import DcLabel from "../components/DcLabel";
import StatusLabel from "../components/StatusLabel";
import StatusBlock from "../components/StatusBlock";
import { getOrders, getProduction, saveProductionDecision, updateOrderStatus } from "../services/api";

function toNumber(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function formatDate(value) {
  if (!value) return "-";
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return "-";
  return parsed.toLocaleDateString();
}

function normalizeOrders(rawOrders) {
  return (rawOrders || []).map((order) => ({
    ...order,
    id: order.orderId,
    items: (order.items || []).map((item) => {
      const rawRequiredProductionQty = item.requiredProductionQty ?? item.decisionRequiredProductionQty;

      return {
        ...item,
        quantity: toNumber(item.quantity),
        pallets: toNumber(item.pallets),
        currentStock: toNumber(item.currentStock),
        requiredStock: toNumber(item.requiredStock),
        difference: toNumber(item.difference),
        remainingStock: toNumber(item.remainingStock),
        productionRequired: toNumber(item.productionRequired),
        requiredProductionQty:
          rawRequiredProductionQty === null ||
          rawRequiredProductionQty === undefined ||
          rawRequiredProductionQty === ""
            ? null
            : toNumber(rawRequiredProductionQty),
      };
    }),
  }));
}

function normalizeProcessedOrders(rawOrders) {
  return (rawOrders || []).map((order) => ({
    orderId: order.id,
    id: order.id,
    orderNumber: order.orderNumber,
    deliveryDate: order.deliveryDate,
    distributionCentreId: order.distributionCentreId,
    distributionCentre: order.distributionCentreName,
    status: order.statusLabel || order.status,
    isProcessed: true,
    items: (order.items || []).map((item) => ({
      orderItemId: item.id,
      productId: item.productId,
      productCode: item.productCode || item.skuCode || "",
      productName: item.productName || "",
      quantity: toNumber(item.quantity),
      pallets: toNumber(item.pallets),
      currentStock: 0,
      requiredStock: toNumber(item.quantity),
      difference: 0,
      productionRequired: 0,
      requiredProductionQty: null,
    })),
  }));
}

function getExistingDecision(item) {
  const hasRequiredProductionQty =
    item.requiredProductionQty !== null &&
    item.requiredProductionQty !== undefined &&
    item.requiredProductionQty !== "";

  if (typeof item.decisionIsSufficient !== "boolean" && !hasRequiredProductionQty) {
    return null;
  }

  return {
    isSufficient: item.decisionIsSufficient === true,
    requiredProductionQty: hasRequiredProductionQty ? toNumber(item.requiredProductionQty) : null,
  };
}

function isRequiredProductionQtySet(value) {
  return value !== null && value !== undefined && value !== "";
}

function normalizeOrderStatus(value) {
  return String(value ?? "")
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]/g, "");
}

function isOrderProcessed(order) {
  if (order?.isProcessed === true) {
    return true;
  }

  const normalizedStatus = normalizeOrderStatus(order?.status);
  return normalizedStatus === "processed" || normalizedStatus === "5";
}

function isOrderEditable(order) {
  const normalizedStatus = normalizeOrderStatus(order?.status);
  return (
    normalizedStatus === "approved" ||
    normalizedStatus === "inproduction" ||
    normalizedStatus === "processed" ||
    normalizedStatus === "6" ||
    normalizedStatus === "5"
  );
}

function ProductionPage() {
  const [orders, setOrders] = useState([]);
  const [expandedOrders, setExpandedOrders] = useState({});
  const [decisionsByOrder, setDecisionsByOrder] = useState({});
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [searchTerm, setSearchTerm] = useState("");
  const [savingItemIds, setSavingItemIds] = useState({});
  const [processingOrderId, setProcessingOrderId] = useState(null);
  const [editedStockByItemId, setEditedStockByItemId] = useState({});

  async function loadProduction() {
    setLoading(true);
    setError("");

    try {
      const [productionResponse, processedResponse] = await Promise.all([
        getProduction(),
        getOrders({ status: "5" }),
      ]);

      console.log("[PRODUCTION FETCH RESPONSE]", productionResponse);
      console.log("[PRODUCTION FETCH PROCESSED RESPONSE]", processedResponse);

      const productionOrders = normalizeOrders(productionResponse?.orders || []);
      const processedOrdersRaw = Array.isArray(processedResponse)
        ? processedResponse
        : Array.isArray(processedResponse?.items)
          ? processedResponse.items
          : [];
      const processedOrders = normalizeProcessedOrders(processedOrdersRaw);

      const mergedByOrderId = new Map();
      productionOrders.forEach((order) => {
        mergedByOrderId.set(order.orderId, order);
      });
      processedOrders.forEach((order) => {
        if (!mergedByOrderId.has(order.orderId)) {
          mergedByOrderId.set(order.orderId, order);
        }
      });

      const allOrders = [...mergedByOrderId.values()].sort((left, right) => {
        const leftDate = new Date(left.deliveryDate || 0).getTime();
        const rightDate = new Date(right.deliveryDate || 0).getTime();

        if (leftDate !== rightDate) {
          return leftDate - rightDate;
        }

        return String(left.orderNumber || "").localeCompare(String(right.orderNumber || ""));
      });
      console.log("[PRODUCTION FETCH] orders array length:", allOrders.length);

      allOrders.forEach((order, index) => {
        const rawStatus = order?.status;
        const normalizedStatus = String(rawStatus || "").trim().toLowerCase();

        console.log("[PRODUCTION ORDER STATUS]", {
          index,
          orderId: order?.orderId,
          orderNumber: order?.orderNumber,
          status: rawStatus,
          statusType: typeof rawStatus,
          normalizedStatus,
        });
      });

      setOrders(allOrders);
      setEditedStockByItemId(() => {
        const nextStockByItemId = {};
        allOrders.forEach((order) => {
          (order.items || []).forEach((item) => {
            nextStockByItemId[item.orderItemId] = toNumber(item.currentStock);
          });
        });

        return nextStockByItemId;
      });

      setExpandedOrders((previous) => {
        if (Object.keys(previous).length > 0) {
          return previous;
        }

        const firstOrder = allOrders[0];
        return firstOrder ? { [firstOrder.orderId]: true } : {};
      });

      setDecisionsByOrder(() => {
        const initial = {};

        allOrders.forEach((order) => {
          const itemDecisions = {};

          order.items.forEach((item) => {
            const existing = getExistingDecision(item);
            if (existing) {
              itemDecisions[item.orderItemId] = existing;
            }
          });

          if (Object.keys(itemDecisions).length > 0) {
            initial[order.orderId] = itemDecisions;
          }
        });

        return initial;
      });
    } catch (requestError) {
      setOrders([]);
      setError(requestError.message || "Failed loading production orders");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    loadProduction();
  }, []);

  const filteredOrders = useMemo(() => {
    const normalizedSearch = searchTerm.trim().toLowerCase();
    if (!normalizedSearch) return orders;

    return orders.filter((order) =>
      String(order.orderNumber || "").toLowerCase().includes(normalizedSearch)
    );
  }, [orders, searchTerm]);

  const hasOrders = orders.length > 0;

  function toggleOrder(orderId) {
    setExpandedOrders((current) => ({
      ...current,
      [orderId]: !current[orderId],
    }));
  }

  function isItemSaving(orderItemId) {
    return Boolean(savingItemIds[orderItemId]);
  }

  function setItemSaving(orderItemId, isSaving) {
    setSavingItemIds((current) => ({
      ...current,
      [orderItemId]: isSaving,
    }));
  }

  function getOrderDecisions(orderId) {
    return decisionsByOrder[orderId] || {};
  }

  function isItemResolved(item, decision) {
    if (decision?.isSufficient === true) {
      return true;
    }

    if (isRequiredProductionQtySet(decision?.requiredProductionQty)) {
      return true;
    }

    if (item?.decisionIsSufficient === true) {
      return true;
    }

    return isRequiredProductionQtySet(item?.requiredProductionQty);
  }

  function isOrderComplete(order) {
    const decisions = getOrderDecisions(order.orderId);
    return order.items.length > 0 && order.items.every((item) => isItemResolved(item, decisions[item.orderItemId]));
  }

  function getEditedInitialStock(item) {
    const editedValue = editedStockByItemId[item.orderItemId];
    if (editedValue === undefined) {
      return toNumber(item.currentStock);
    }

    return toNumber(editedValue);
  }

  function updateEditedInitialStock(orderItemId, value) {
    const parsedValue = Number(value);
    const normalizedValue = Number.isFinite(parsedValue) ? Math.max(0, parsedValue) : 0;

    setEditedStockByItemId((current) => ({
      ...current,
      [orderItemId]: normalizedValue,
    }));
  }

  async function submitDecision(order, item, isSufficient) {
    const editedInitialStock = getEditedInitialStock(item);

    setItemSaving(item.orderItemId, true);
    setError("");

    try {
      const payload = {
        orderId: order.orderId,
        decisions: [
          {
            orderItemId: item.orderItemId,
            isSufficient,
            requiredProductionQty: 0,
            manualInitialStock: editedInitialStock,
            notes: `Manual stock entered: ${editedInitialStock}`,
          },
        ],
      };

      await saveProductionDecision(payload);
      await loadProduction();
    } catch (requestError) {
      setError(requestError.message || "Failed to save production decision");
    } finally {
      setItemSaving(item.orderItemId, false);
    }
  }

  async function processOrder(order) {
    if (!order?.orderId) {
      return;
    }

    setProcessingOrderId(order.orderId);
    setError("");

    try {
      await updateOrderStatus(order.orderId, "Processed");
      window.dispatchEvent(new Event("orders:refresh"));
      await loadProduction();
    } catch (requestError) {
      console.error("Failed processing order:", requestError);
      setError("Insufficient stock to process this order");
    } finally {
      setProcessingOrderId(null);
    }
  }

  function handlePrimaryOrderAction(order) {
    if (isOrderProcessed(order)) {
      toggleOrder(order.orderId);
      return;
    }

    void processOrder(order);
  }

  return (
    <section>
      <header className="page-header">
        <h2>Production Workflow</h2>
        <p>Review approved and processed orders, then confirm stock decisions per item.</p>
      </header>

      <div className="panel" style={{ marginBottom: 16 }}>
        <div style={{ maxWidth: 360 }}>
          <label htmlFor="production-order-search">Search Order Number</label>
          <input
            id="production-order-search"
            type="text"
            placeholder="e.g. ORD-10021"
            value={searchTerm}
            onChange={(event) => setSearchTerm(event.target.value)}
          />
        </div>

        <StatusBlock
          loading={loading}
          error={error}
          empty={!loading && !error && !hasOrders}
          loadingText="Loading production orders..."
          emptyText="No production orders found"
          spinner
        />
      </div>

      {!loading && !error && hasOrders && filteredOrders.length === 0 && (
        <div className="panel" style={{ marginBottom: 16 }}>
          <p className="status-text">No matching order number found</p>
        </div>
      )}

      {!loading && !error &&
        filteredOrders.map((order) => {
          const decisions = getOrderDecisions(order.orderId);
          const expanded = Boolean(expandedOrders[order.orderId]);
          const complete = isOrderComplete(order);
          const processed = isOrderProcessed(order);
          const editable = isOrderEditable(order);
          const hasProductionRequired = (order.items || []).some(
            (item) => Number(item?.productionRequired ?? 0) > 0
          );
          const disableProcess =
            !complete ||
            !editable ||
            hasProductionRequired ||
            processingOrderId === order.orderId;

          return (
            <article key={order.orderId} className="panel" style={{ marginBottom: 14 }}>
              <button
                type="button"
                onClick={() => toggleOrder(order.orderId)}
                style={{
                  width: "100%",
                  border: "none",
                  background: "transparent",
                  textAlign: "left",
                  padding: 0,
                  cursor: "pointer",
                }}
              >
                <div className="section-heading">
                  <h3 style={{ marginBottom: 4 }}>
                    {expanded ? "v" : ">"} Order {order.orderNumber || order.orderId}
                  </h3>
                  <StatusLabel status={order.status} label={order.status} />
                </div>
                <p className="status-text" style={{ marginBottom: 4 }}>
                  Delivery: {formatDate(order.deliveryDate)}
                </p>
                <p className="status-text">Distribution Centre: <DcLabel row={order} /></p>
              </button>

              {expanded && (
                <div style={{ marginTop: 12, overflowX: "auto" }}>
                  <table style={{ width: "100%", borderCollapse: "collapse" }}>
                    <thead>
                      <tr>
                        <th style={{ textAlign: "left", padding: "8px 6px" }}>Product</th>
                        <th style={{ textAlign: "right", padding: "8px 6px" }}>Quantity</th>
                        <th style={{ textAlign: "right", padding: "8px 6px" }}>Pallets</th>
                        <th style={{ textAlign: "right", padding: "8px 6px" }}>Entered Stock</th>
                        <th style={{ textAlign: "right", padding: "8px 6px" }}>Required Stock</th>
                        <th style={{ textAlign: "right", padding: "8px 6px" }}>Stock Leftover</th>
                        <th style={{ textAlign: "left", padding: "8px 6px" }}>Decision</th>
                      </tr>
                    </thead>
                    <tbody>
                      {order.items.map((item) => {
                        const itemDecision = decisions[item.orderItemId];
                        const saving = isItemSaving(item.orderItemId);
                        const currentStockValue = getEditedInitialStock(item);
                        const hasRemainingStock = item.remainingStock !== undefined && item.remainingStock !== null;
                        const stockLeftover = hasRemainingStock
                          ? Number(item.remainingStock)
                          : toNumber(item.currentStock) - toNumber(item.requiredStock);
                        const isShortage = stockLeftover < 0;

                        console.log("[UI STOCK VALUE]", item.productName, currentStockValue);

                        return (
                          <tr key={item.orderItemId}>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee" }}>
                              {item.productName || "Unknown"}
                            </td>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee", textAlign: "right" }}>
                              {toNumber(item.quantity).toFixed(0)}
                            </td>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee", textAlign: "right" }}>
                              {toNumber(item.pallets).toFixed(2)}
                            </td>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee", textAlign: "right" }}>
                              <input
                                type="number"
                                min="0"
                                step="0.01"
                                value={currentStockValue}
                                disabled={!editable}
                                onChange={(event) => updateEditedInitialStock(item.orderItemId, event.target.value)}
                                style={{ width: 90, textAlign: "right" }}
                              />
                            </td>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee", textAlign: "right" }}>
                              {toNumber(item.requiredStock).toFixed(0)}
                            </td>
                            <td
                              style={{
                                padding: "8px 6px",
                                borderTop: "1px solid #eee",
                                textAlign: "right",
                                color: isShortage ? "var(--danger-color, #b00020)" : "inherit",
                                fontWeight: isShortage ? 700 : 400,
                              }}
                            >
                              {stockLeftover.toFixed(2)}
                            </td>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee" }}>
                              <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                                <span
                                  className={isShortage ? "badge orange" : "badge green"}
                                  style={{ alignSelf: "center" }}
                                >
                                  {isShortage ? "Shortage" : "Stock Leftover OK"}: {stockLeftover.toFixed(2)}
                                </span>
                                {editable && (
                                  <>
                                    <button
                                      type="button"
                                      className="btn-success"
                                      disabled={saving}
                                      onClick={() => submitDecision(order, item, true)}
                                    >
                                      OK
                                    </button>
                                    <button
                                      type="button"
                                      className="btn-warning"
                                      disabled={saving}
                                      onClick={() => submitDecision(order, item, false)}
                                    >
                                      Produce More
                                    </button>
                                  </>
                                )}
                                {itemDecision && (
                                  <span className="badge green" style={{ alignSelf: "center" }}>
                                    {itemDecision.isSufficient ? "OK saved" : "Produce More saved"}
                                  </span>
                                )}
                              </div>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>

                  <div style={{ marginTop: 10, display: "flex", gap: 12, alignItems: "center", flexWrap: "wrap" }}>
                    <span className={complete ? "badge green" : "badge orange"}>
                      {complete ? "All items handled" : "Pending item decisions"}
                    </span>
                    <button
                      type="button"
                      disabled={disableProcess}
                      onClick={() => handlePrimaryOrderAction(order)}
                    >
                      {processed ? "Edit Order" : "Process Order"}
                    </button>
                  </div>
                </div>
              )}
            </article>
          );
        })}
    </section>
  );
}

export default ProductionPage;
