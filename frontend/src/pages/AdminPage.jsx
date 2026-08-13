import { useEffect, useMemo, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import DataTable from "../components/DataTable";
import ConfirmDeleteModal from "../components/ConfirmDeleteModal";
import DcLabel from "../components/DcLabel";
import MultiDcFilter from "../components/MultiDcFilter";
import PromoPriceDisplay from "../components/PromoPriceDisplay";
import StatusBlock from "../components/StatusBlock";
import {
  createDistributionCentre,
  createProduct,
  deleteDistributionCentre,
  deletePriceList,
  deleteProduct,
  formatCurrency,
  deletePricePromotion,
  getDistributionCentres,
  getPriceLists,
  getPricePromotions,
  getProducts,
  resetTestData,
  updatePricePromotion,
  upsertPricePromotion,
  upsertPriceList,
  updateProduct,
} from "../services/api";
import { getPromoState } from "../utils/promoPricing";

const defaultProduct = { id: null, name: "", skuCode: "", palletConversionRate: "" };

function isProductUnmapped(product) {
  return product?.isMapped === false || Boolean(product?.requiresAttention);
}

function isRestoredResponse(payload) {
  if (!payload || typeof payload !== "object") {
    return false;
  }

  if (payload.restored === true || payload.isRestored === true || payload.reactivated === true) {
    return true;
  }

  const restoredCount = Number(payload.restoredCount ?? 0);
  if (Number.isFinite(restoredCount) && restoredCount > 0) {
    return true;
  }

  const message = String(payload.message ?? payload.statusMessage ?? "").toLowerCase();
  return message.includes("restored") || message.includes("reactivated");
}

function isAlreadyExistsError(error) {
  const errorMessage = String(error?.message ?? "").toLowerCase();
  const payloadMessage = String(error?.payload?.message ?? "").toLowerCase();
  return errorMessage.includes("already exists") || payloadMessage.includes("already exists");
}

function AdminPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const [products, setProducts] = useState([]);
  const [priceLists, setPriceLists] = useState([]);
  const [distributionCentres, setDistributionCentres] = useState([]);
  const [promotions, setPromotions] = useState([]);

  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [messageType, setMessageType] = useState("success");
  const [submitting, setSubmitting] = useState(false);
  const [resetting, setResetting] = useState(false);
  const [showUnmappedOnly, setShowUnmappedOnly] = useState(false);
  const [deleteDialog, setDeleteDialog] = useState(null);
  const [deleting, setDeleting] = useState(false);

  const [productForm, setProductForm] = useState(defaultProduct);
  const [priceListForm, setPriceListForm] = useState({ productId: "", distributionCentreIds: [], price: "" });
  const [distributionCentreName, setDistributionCentreName] = useState("");
  const [selectedPriceListDcIds, setSelectedPriceListDcIds] = useState([]);
  const [selectedPromoDcIds, setSelectedPromoDcIds] = useState([]);
  const [promoForm, setPromoForm] = useState({
    id: null,
    productId: "",
    distributionCentreIds: [],
    promoPrice: "",
    startDate: "",
    endDate: "",
  });

  const filteredProducts = useMemo(() => {
    const nextProducts = showUnmappedOnly
      ? products.filter((product) => isProductUnmapped(product))
      : products;

    return [...nextProducts].sort((left, right) => {
      const leftPriority = isProductUnmapped(left) ? 0 : 1;
      const rightPriority = isProductUnmapped(right) ? 0 : 1;

      if (leftPriority !== rightPriority) {
        return leftPriority - rightPriority;
      }

      return String(left.name ?? "").localeCompare(String(right.name ?? ""));
    });
  }, [products, showUnmappedOnly]);

  const unmappedProductCount = useMemo(() => products.filter((product) => isProductUnmapped(product)).length, [products]);

  const filteredPriceLists = useMemo(() => {
    if (!selectedPriceListDcIds.length) {
      return priceLists;
    }

    return priceLists.filter((row) => selectedPriceListDcIds.includes(Number(row.distributionCentreId)));
  }, [priceLists, selectedPriceListDcIds]);

  const promoPrices = useMemo(() => {
    return priceLists
      .filter((row) => row.promoPrice !== null && row.promoPrice !== undefined)
      .map((row) => {
        const matchedPromo = promotions.find(
          (p) =>
            Number(p.productId) === Number(row.productId) &&
            Number(p.distributionCentreId) === Number(row.distributionCentreId)
        );
        return {
          id: matchedPromo?.id ?? null,
          productId: Number(row.productId),
          distributionCentreIds: [Number(row.distributionCentreId)],
          promoPrice: Number(row.promoPrice),
          startDate: String(row.promoStartDate ?? "").slice(0, 10),
          endDate: String(row.promoEndDate ?? "").slice(0, 10),
          basePrice: Number(row.basePrice),
          effectivePrice: Number(row.effectivePrice),
        };
      });
  }, [priceLists, promotions]);

  const visiblePromos = useMemo(() => {
    if (!selectedPromoDcIds.length) {
      return promoPrices;
    }

    return promoPrices.filter((promo) =>
      promo.distributionCentreIds.some((distributionCentreId) =>
        selectedPromoDcIds.includes(Number(distributionCentreId))
      )
    );
  }, [promoPrices, selectedPromoDcIds]);

  const activePromos = useMemo(
    () => visiblePromos.filter((promo) => getPromoState(promo) === "active"),
    [visiblePromos]
  );

  const expiredPromos = useMemo(
    () => visiblePromos.filter((promo) => getPromoState(promo) === "expired"),
    [visiblePromos]
  );

  function getDistributionCentreNames(ids) {
    if (!Array.isArray(ids)) {
      return [];
    }

    return ids
      .map((id) => distributionCentres.find((centre) => Number(centre.id) === Number(id))?.name)
      .filter(Boolean);
  }

  function clearPromoForm() {
    setPromoForm({
      id: null,
      productId: "",
      distributionCentreIds: [],
      promoPrice: "",
      startDate: "",
      endDate: "",
    });
  }

  async function loadAdminData() {
    setLoading(true);
    setError("");

    try {
      const [productsData, priceListsData, distributionCentresData, promotionsData] = await Promise.all([
        getProducts(),
        getPriceLists(),
        getDistributionCentres(),
        getPricePromotions(),
      ]);

      setProducts(Array.isArray(productsData) ? productsData : []);
      setPriceLists(Array.isArray(priceListsData) ? priceListsData : []);
      setDistributionCentres(Array.isArray(distributionCentresData) ? distributionCentresData : []);
      setPromotions(Array.isArray(promotionsData) ? promotionsData : []);
    } catch (requestError) {
      setError(requestError.message || "Failed loading admin data");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    loadAdminData();
  }, []);

  useEffect(() => {
    function handleLookupsRefresh() {
      void loadAdminData();
    }

    window.addEventListener("lookups:refresh", handleLookupsRefresh);

    return () => {
      window.removeEventListener("lookups:refresh", handleLookupsRefresh);
    };
  }, []);

  useEffect(() => {
    const mapProduct = location.state?.mapProduct;
    if (!mapProduct) {
      return;
    }

    setProductForm({
      id: mapProduct.id ?? null,
      name: mapProduct.name ?? "",
      skuCode: mapProduct.skuCode ?? "",
      palletConversionRate: mapProduct.palletConversionRate ?? "1",
    });

    navigate(location.pathname, { replace: true, state: null });
  }, [location.pathname, location.state, navigate]);

  async function submitProduct(event) {
    event.preventDefault();
    setMessage("");
    setSubmitting(true);

    const payload = {
      name: productForm.name,
      skuCode: productForm.skuCode,
      palletConversionRate: Number(productForm.palletConversionRate),
    };

    try {
      if (productForm.id) {
        await updateProduct(productForm.id, payload);
        setMessage("Product updated successfully. Related orders recalculated.");
        setMessageType("success");
      } else {
        const response = await createProduct(payload);

        if (isRestoredResponse(response)) {
          setMessage("This item already existed and has been restored");
        } else {
          setMessage("Product created successfully");
        }

        setMessageType("success");
      }

      setProductForm(defaultProduct);
      window.dispatchEvent(new Event("orders:refresh"));
      window.dispatchEvent(new Event("lookups:refresh"));
      await loadAdminData();
    } catch (submitError) {
      if (isAlreadyExistsError(submitError)) {
        setMessage("This item already exists");
      } else {
        setMessage(submitError.message || "Failed saving product");
      }

      setMessageType("error");
    } finally {
      setSubmitting(false);
    }
  }

  async function submitPriceList(event) {
    event.preventDefault();
    setMessage("");

    const normalizedDcIds = Array.isArray(priceListForm.distributionCentreIds)
      ? [...new Set(priceListForm.distributionCentreIds.map((value) => Number(value)).filter((value) => Number.isFinite(value) && value > 0))]
      : [];

    const payload = {
      productId: Number(priceListForm.productId),
      distributionCentreIds: normalizedDcIds,
      distributionCentreId: normalizedDcIds.length === 1 ? normalizedDcIds[0] : null,
      price: Number(priceListForm.price),
    };

    if (!payload.productId || Number.isNaN(payload.productId)) {
      setMessage("Please select a product.");
      return;
    }

    if (!normalizedDcIds.length) {
      setMessage("Please select at least one distribution centre.");
      return;
    }

    if (!payload.price || Number.isNaN(payload.price)) {
      setMessage("Please enter a valid price.");
      return;
    }

    setSubmitting(true);

    try {
      const response = await upsertPriceList(payload);

      if (isRestoredResponse(response)) {
        setMessage("This item already existed and has been restored");
      } else {
        setMessage(normalizedDcIds.length > 1 ? "Price list created successfully for selected distribution centres" : "Price list created successfully");
      }

      setMessageType("success");
      setPriceListForm({ productId: "", distributionCentreIds: [], price: "" });
      window.dispatchEvent(new Event("orders:refresh"));
      window.dispatchEvent(new Event("lookups:refresh"));
      await loadAdminData();
    } catch (submitError) {
      if (isAlreadyExistsError(submitError)) {
        setMessage("This item already exists");
      } else {
        setMessage(submitError.message || "Failed saving price list");
      }

      setMessageType("error");
    } finally {
      setSubmitting(false);
    }
  }

  async function handleCreateDistributionCentre() {
    console.log("🔥 STEP 1: Button clicked");
    console.log("📦 STEP 2: Current input value:", distributionCentreName);

    if (!distributionCentreName || !distributionCentreName.trim()) {
      console.warn("⚠️ STEP 3: Validation failed - empty name");
      setMessage("Please enter a distribution centre name.");
      return;
    }

    setMessage("");
    setSubmitting(true);

    try {
      console.log("🚀 STEP 4: About to call API");
      const payload = { name: distributionCentreName.trim() };
      console.log("Submitting payload:", payload);
      const response = await createDistributionCentre(payload);
      console.log("✅ STEP 5: API SUCCESS", response);

      if (isRestoredResponse(response)) {
        setMessage("This item already existed and has been restored");
      } else {
        setMessage("Distribution centre created successfully");
      }

      setMessageType("success");
      setDistributionCentreName("");
      window.dispatchEvent(new Event("lookups:refresh"));
      await loadAdminData();
    } catch (err) {
      console.error("❌ STEP 6: API FAILED", err);
      if (isAlreadyExistsError(err)) {
        setMessage("This item already exists");
      } else {
        setMessage(err.message || "Failed saving distribution centre");
      }

      setMessageType("error");
    } finally {
      setSubmitting(false);
    }
  }

  async function handleResetTestData() {
    const confirmed = window.confirm("Are you sure? This will delete ALL orders.");
    if (!confirmed) {
      return;
    }

    setMessage("");
    setResetting(true);

    try {
      await resetTestData();
      setMessage("Test data reset successfully");
      setMessageType("success");
      window.dispatchEvent(new Event("orders:refresh"));
      await loadAdminData();
    } catch (resetError) {
      setMessage(resetError.message || "Failed resetting test data");
      setMessageType("error");
    } finally {
      setResetting(false);
    }
  }

  function validatePromoForm() {
    const productId = Number(promoForm.productId);
    const distributionCentreIds = promoForm.distributionCentreIds.map((value) => Number(value));
    const promoPrice = Number(promoForm.promoPrice);

    if (!productId || Number.isNaN(productId)) {
      return "Please select a product for the promo.";
    }

    if (!distributionCentreIds.length) {
      return "Please select at least one distribution centre for the promo.";
    }

    if (!promoPrice || Number.isNaN(promoPrice)) {
      return "Please enter a valid promo price.";
    }

    if (!promoForm.startDate || !promoForm.endDate) {
      return "Please provide both promo start and end dates.";
    }

    if (promoForm.endDate < promoForm.startDate) {
      return "Promo end date must be on or after the start date.";
    }

    return "";
  }

  async function submitPromoPrice(event) {
    event.preventDefault();
    setMessage("");

    const validationError = validatePromoForm();
    if (validationError) {
      setMessage(validationError);
      setMessageType("error");
      return;
    }

    setSubmitting(true);

    try {
      const distributionCentreIds = promoForm.distributionCentreIds.map((value) => Number(value));

      if (promoForm.id) {
        const distributionCentreId = distributionCentreIds[0];

        await updatePricePromotion(promoForm.id, {
          productId: Number(promoForm.productId),
          distributionCentreId,
          promoPrice: Number(promoForm.promoPrice),
          startDate: promoForm.startDate,
          endDate: promoForm.endDate,
          isActive: true,
        });
      } else {
        await Promise.all(
          distributionCentreIds.map((distributionCentreId) =>
            upsertPricePromotion({
              productId: Number(promoForm.productId),
              distributionCentreId,
              promoPrice: Number(promoForm.promoPrice),
              startDate: promoForm.startDate,
              endDate: promoForm.endDate,
              isActive: true,
            })
          )
        );
      }

      setMessage(promoForm.id ? "Promo price updated successfully" : "Promo price created successfully");
      setMessageType("success");
      clearPromoForm();
      window.dispatchEvent(new Event("orders:refresh"));
      await loadAdminData();
    } catch (submitError) {
      setMessage(submitError.message || "Failed saving promo price");
      setMessageType("error");
    } finally {
      setSubmitting(false);
    }
  }

  function editPromoPrice(promo) {
    setPromoForm({
      id: promo.id,
      productId: String(promo.productId),
      distributionCentreIds: promo.distributionCentreIds.map((value) => Number(value)),
      promoPrice: String(promo.promoPrice),
      startDate: promo.startDate,
      endDate: promo.endDate,
    });
  }

  async function deletePromoPrice(id) {
    setMessage("");
    setSubmitting(true);

    try {
      await deletePricePromotion(id);
      setMessage("Promo price deleted successfully");
      setMessageType("success");

      if (promoForm.id === id) {
        clearPromoForm();
      }

      window.dispatchEvent(new Event("orders:refresh"));
      await loadAdminData();
    } catch (deleteError) {
      setMessage(deleteError.message || "Failed deleting promo price");
      setMessageType("error");
    } finally {
      setSubmitting(false);
    }
  }

  function openDeleteDialog(payload) {
    setDeleteDialog(payload);
  }

  function closeDeleteDialog() {
    if (deleting) {
      return;
    }

    setDeleteDialog(null);
  }

  async function confirmDelete() {
    if (!deleteDialog) {
      return;
    }

    setMessage("");
    setDeleting(true);

    try {
      if (deleteDialog.type === "product") {
        await deleteProduct(deleteDialog.id);
        setProducts((current) => current.filter((item) => item.id !== deleteDialog.id));
        setPriceLists((current) => current.filter((item) => item.productId !== deleteDialog.id));

        setProductForm((current) => (current.id === deleteDialog.id ? defaultProduct : current));
        if (String(priceListForm.productId) === String(deleteDialog.id)) {
          setPriceListForm((current) => ({ ...current, productId: "" }));
        }

        setMessage("Product deleted successfully");
        setMessageType("success");
      } else if (deleteDialog.type === "pricelist") {
        await deletePriceList(deleteDialog.id);
        setPriceLists((current) => current.filter((item) => item.id !== deleteDialog.id));
        setMessage("Price list deleted successfully");
        setMessageType("success");
        window.dispatchEvent(new Event("orders:refresh"));
      } else if (deleteDialog.type === "distributionCentre") {
        await deleteDistributionCentre(deleteDialog.id);
        setDistributionCentres((current) => current.filter((item) => item.id !== deleteDialog.id));
        setPriceLists((current) => current.filter((item) => item.distributionCentreId !== deleteDialog.id));

        if (Array.isArray(priceListForm.distributionCentreIds) && priceListForm.distributionCentreIds.includes(Number(deleteDialog.id))) {
          setPriceListForm((current) => ({
            ...current,
            distributionCentreIds: current.distributionCentreIds.filter((value) => Number(value) !== Number(deleteDialog.id)),
          }));
        }

        setMessage("Distribution centre deleted successfully");
        setMessageType("success");
      }
    } catch (requestError) {
      const backendMessage = requestError?.payload?.message || requestError?.message || "Delete failed";
      window.alert(backendMessage);
      setMessage(backendMessage);
      setMessageType("error");
    } finally {
      setDeleting(false);
      setDeleteDialog(null);
    }
  }

  return (
    <section>
      <header className="page-header">
        <h2>Admin</h2>
        <p>Manage products, price lists, and distribution centres.</p>
      </header>

      <StatusBlock loading={loading} error={error} spinner />
      {message && (
        <p className={messageType === "error" ? "alert error" : "alert success"}>
          {message}
        </p>
      )}

      <div className="panel">
        <div className="section-heading">
          <h3>Products</h3>
          <div className="action-row">
            <span className="status-text">{unmappedProductCount} product{unmappedProductCount === 1 ? "" : "s"} need review</span>
            <button
              type="button"
              className="secondary"
              onClick={() => setShowUnmappedOnly((current) => !current)}
            >
              {showUnmappedOnly ? "Show All Products" : "Show Unmapped Only"}
            </button>
          </div>
        </div>
        <form onSubmit={submitProduct} className="grid-2">
          <div>
            <label>Product Name</label>
            <input
              required
              value={productForm.name}
              onChange={(event) =>
                setProductForm((current) => ({ ...current, name: event.target.value }))
              }
            />
          </div>

          <div>
            <label>SKU</label>
            <input
              required
              value={productForm.skuCode}
              onChange={(event) =>
                setProductForm((current) => ({ ...current, skuCode: event.target.value }))
              }
            />
          </div>

          <div>
            <label>Pallet Conversion Rate</label>
            <input
              required
              type="number"
              min="0.01"
              step="0.01"
              value={productForm.palletConversionRate}
              onChange={(event) =>
                setProductForm((current) => ({ ...current, palletConversionRate: event.target.value }))
              }
            />
          </div>

          <div style={{ display: "flex", alignItems: "end", gap: 8 }}>
            <button type="submit" disabled={submitting}>
              {submitting ? "Saving..." : productForm.id ? "Update" : "Create"}
            </button>
            {productForm.id && (
              <button
                type="button"
                className="secondary"
                onClick={() => setProductForm(defaultProduct)}
              >
                Cancel edit
              </button>
            )}
          </div>
        </form>

        {!loading && filteredProducts.length > 0 && (
          <DataTable
            columns={[
              { key: "name", header: "Name" },
              { key: "skuCode", header: "SKU" },
              { key: "palletConversionRate", header: "Pallet Conversion Rate" },
              {
                key: "requiresAttention",
                header: "Status",
                render: (row) => isProductUnmapped(row)
                  ? (
                    <span
                      className="status-chip warning"
                      title="This product was created automatically from CSV and needs review"
                    >
                      Unmapped
                    </span>
                  )
                  : <span className="status-chip success">Mapped</span>,
              },
              {
                key: "actions",
                header: "Actions",
                render: (row) => (
                  <div className="action-row">
                    <button
                      type="button"
                      className="secondary"
                      onClick={() =>
                        setProductForm({
                          id: row.id,
                          name: row.name,
                          skuCode: row.skuCode,
                          palletConversionRate: row.palletConversionRate,
                        })
                      }
                    >
                      Edit
                    </button>
                    <button
                      type="button"
                      className="danger"
                      onClick={() =>
                        openDeleteDialog({
                          type: "product",
                          id: row.id,
                          title: "Delete Product",
                          message: `Are you sure you want to delete this? (${row.name})`,
                        })
                      }
                    >
                      Delete
                    </button>
                  </div>
                ),
              },
            ]}
            data={filteredProducts}
            rowKey="id"
            rowClassName={(row) => isProductUnmapped(row) ? "row-unmapped-product" : ""}
            sortKey=""
            sortDirection="asc"
            onSort={() => {}}
          />
        )}

        {!loading && !error && filteredProducts.length === 0 && (
          <p className="status-text">{showUnmappedOnly ? "No unmapped products found" : "No data found"}</p>
        )}
      </div>

      <div className="panel">
        <h3>Price Lists</h3>
        <div style={{ marginBottom: 12 }}>
          <MultiDcFilter
            label="Filter Price Lists By Distribution Centres"
            distributionCentres={distributionCentres}
            selectedIds={selectedPriceListDcIds}
            onChange={setSelectedPriceListDcIds}
          />
        </div>
        <form onSubmit={submitPriceList} className="grid-2">
          <div>
            <label>Product</label>
            <select
              required
              value={priceListForm.productId}
              onChange={(event) =>
                setPriceListForm((current) => ({ ...current, productId: event.target.value }))
              }
            >
              <option value="">Select Product</option>
              {products.map((product) => (
                <option key={product.id} value={product.id}>
                  {product.name}
                </option>
              ))}
            </select>
          </div>

          <div>
            <MultiDcFilter
              label="Distribution Centres"
              distributionCentres={distributionCentres}
              selectedIds={priceListForm.distributionCentreIds}
              onChange={(value) =>
                setPriceListForm((current) => ({
                  ...current,
                  distributionCentreIds: value,
                }))
              }
            />
          </div>

          <div>
            <label>Price</label>
            <input
              required
              type="number"
              min="0.01"
              step="0.01"
              value={priceListForm.price}
              onChange={(event) =>
                setPriceListForm((current) => ({ ...current, price: event.target.value }))
              }
            />
          </div>

          <div style={{ display: "flex", alignItems: "end" }}>
            <button type="submit" disabled={submitting}>
              {submitting ? "Saving..." : "Save Price List"}
            </button>
          </div>
        </form>

        {!loading && filteredPriceLists.length > 0 && (
          <DataTable
            columns={[
              { key: "productName", header: "Product" },
              {
                key: "distributionCentreName",
                header: "Distribution Centre",
                render: (row) => <DcLabel row={row} />,
              },
              {
                key: "price",
                header: "Price",
                render: (row) => {
                  const promo = row.promoPrice !== null && row.promoPrice !== undefined
                    ? {
                        promoPrice: Number(row.promoPrice),
                        startDate: String(row.promoStartDate ?? "").slice(0, 10),
                        endDate: String(row.promoEndDate ?? "").slice(0, 10),
                      }
                    : null;

                  return <PromoPriceDisplay basePrice={row.basePrice} promo={promo} compact />;
                },
              },
              {
                key: "actions",
                header: "Actions",
                render: (row) => (
                  <button
                    type="button"
                    className="danger"
                    onClick={() =>
                      openDeleteDialog({
                        type: "pricelist",
                        id: row.id,
                        title: "Delete Price List",
                        message: `Are you sure you want to delete this? (${row.productName} @ ${row.distributionCentreName})`,
                      })
                    }
                  >
                    Delete
                  </button>
                ),
              },
            ]}
            data={filteredPriceLists}
            rowKey="id"
            sortKey=""
            sortDirection="asc"
            onSort={() => {}}
          />
        )}

        {!loading && !error && filteredPriceLists.length === 0 && (
          <p className="status-text">No data found</p>
        )}
      </div>

      <div className="panel">
        <h3>Promo Prices</h3>
        <p className="status-text">Promos apply to specific products and distribution centres, then revert to normal DC price when expired.</p>

        <div style={{ marginBottom: 12 }}>
          <MultiDcFilter
            label="Filter Promos By Distribution Centres"
            distributionCentres={distributionCentres}
            selectedIds={selectedPromoDcIds}
            onChange={setSelectedPromoDcIds}
          />
        </div>

        <form onSubmit={submitPromoPrice} className="grid-2">
          <div>
            <label>Product</label>
            <select
              required
              value={promoForm.productId}
              onChange={(event) =>
                setPromoForm((current) => ({ ...current, productId: event.target.value }))
              }
            >
              <option value="">Select Product</option>
              {products.map((product) => (
                <option key={product.id} value={product.id}>
                  {product.name}
                </option>
              ))}
            </select>
          </div>

          <MultiDcFilter
            label="Distribution Centres"
            distributionCentres={distributionCentres}
            selectedIds={promoForm.distributionCentreIds}
            onChange={(ids) =>
              setPromoForm((current) => ({
                ...current,
                distributionCentreIds: ids.map((value) => Number(value)),
              }))
            }
          />

          <div>
            <label>Promo Price</label>
            <input
              required
              type="number"
              min="0.01"
              step="0.01"
              value={promoForm.promoPrice}
              onChange={(event) =>
                setPromoForm((current) => ({ ...current, promoPrice: event.target.value }))
              }
            />
          </div>

          <div>
            <label>Start Date</label>
            <input
              required
              type="date"
              value={promoForm.startDate}
              onChange={(event) =>
                setPromoForm((current) => ({ ...current, startDate: event.target.value }))
              }
            />
          </div>

          <div>
            <label>End Date</label>
            <input
              required
              type="date"
              value={promoForm.endDate}
              onChange={(event) =>
                setPromoForm((current) => ({ ...current, endDate: event.target.value }))
              }
            />
          </div>

          <div style={{ display: "flex", alignItems: "end", gap: 8 }}>
            <button type="submit">{promoForm.id ? "Update Promo" : "Create Promo"}</button>
            {promoForm.id && (
              <button type="button" className="secondary" onClick={clearPromoForm}>
                Cancel edit
              </button>
            )}
          </div>
        </form>

        <div className="grid-2" style={{ marginTop: 14 }}>
          <div>
            <h4>Active Promos</h4>
            {activePromos.length === 0 ? (
              <p className="status-text">No active promos</p>
            ) : (
              <DataTable
                columns={[
                  {
                    key: "productId",
                    header: "Product",
                    render: (row) => products.find((product) => Number(product.id) === Number(row.productId))?.name || "-",
                  },
                  {
                    key: "distributionCentreIds",
                    header: "Distribution Centres",
                    render: (row) => getDistributionCentreNames(row.distributionCentreIds).join(", ") || "-",
                  },
                  {
                    key: "promoPrice",
                    header: "Promo",
                    render: (row) => {
                      const basePrice = Number.isFinite(Number(row.basePrice)) ? row.basePrice : row.effectivePrice;
                      return <PromoPriceDisplay basePrice={basePrice} promo={row} compact />;
                    },
                  },
                  {
                    key: "actions",
                    header: "Actions",
                    render: (row) => (
                      <div className="action-row">
                        <button type="button" className="secondary" onClick={() => editPromoPrice(row)} disabled={!row.id}>
                          Edit
                        </button>
                        <button type="button" className="danger" onClick={() => deletePromoPrice(row.id)} disabled={!row.id}>
                          Delete
                        </button>
                      </div>
                    ),
                  },
                ]}
                data={activePromos}
                rowKey="id"
                sortKey=""
                sortDirection="asc"
                onSort={() => {}}
              />
            )}
          </div>

          <div>
            <h4>Expired Promos</h4>
            {expiredPromos.length === 0 ? (
              <p className="status-text">No expired promos</p>
            ) : (
              <DataTable
                columns={[
                  {
                    key: "productId",
                    header: "Product",
                    render: (row) => products.find((product) => Number(product.id) === Number(row.productId))?.name || "-",
                  },
                  {
                    key: "distributionCentreIds",
                    header: "Distribution Centres",
                    render: (row) => getDistributionCentreNames(row.distributionCentreIds).join(", ") || "-",
                  },
                  {
                    key: "promoPrice",
                    header: "Price State",
                    render: (row) => {
                      const basePrice = Number.isFinite(Number(row.basePrice)) ? row.basePrice : row.effectivePrice;
                      return <PromoPriceDisplay basePrice={basePrice} promo={row} compact />;
                    },
                  },
                ]}
                data={expiredPromos}
                rowKey="id"
                sortKey=""
                sortDirection="asc"
                onSort={() => {}}
              />
            )}
          </div>
        </div>
      </div>

      <div className="panel">
        <h3>Distribution Centres</h3>
        <div className="grid-2">
          <div>
            <label>Name</label>
            <input
              value={distributionCentreName}
              onChange={(event) => setDistributionCentreName(event.target.value)}
              placeholder="Distribution centre name"
            />
          </div>

          <div style={{ display: "flex", alignItems: "end" }}>
            <button type="button" disabled={submitting} onClick={handleCreateDistributionCentre}>
              {submitting ? "Saving..." : "Create"}
            </button>
          </div>
        </div>

        {!loading && distributionCentres.length > 0 && (
          <DataTable
            columns={[
              { key: "name", header: "Name" },
              {
                key: "actions",
                header: "Actions",
                render: (row) => (
                  <button
                    type="button"
                    className="danger"
                    onClick={() =>
                      openDeleteDialog({
                        type: "distributionCentre",
                        id: row.id,
                        title: "Delete Distribution Centre",
                        message: `Are you sure you want to delete this? (${row.name})`,
                      })
                    }
                  >
                    Delete
                  </button>
                ),
              },
            ]}
            data={distributionCentres}
            rowKey="id"
            sortKey=""
            sortDirection="asc"
            onSort={() => {}}
          />
        )}

        {!loading && !error && distributionCentres.length === 0 && (
          <p className="status-text">No data found</p>
        )}
      </div>

      <div className="panel">
        <h3>Test Data</h3>
        <p className="status-text">Use this to clear all orders and reset dashboard test data.</p>
        <button
          type="button"
          className="danger"
          onClick={handleResetTestData}
          disabled={resetting || submitting}
        >
          {resetting ? "Resetting..." : "Reset Test Data"}
        </button>
      </div>

      <ConfirmDeleteModal
        open={Boolean(deleteDialog)}
        title={deleteDialog?.title}
        message={deleteDialog?.message}
        confirmText="Delete"
        confirming={deleting}
        onCancel={closeDeleteDialog}
        onConfirm={confirmDelete}
      />
    </section>
  );
}

export default AdminPage;
