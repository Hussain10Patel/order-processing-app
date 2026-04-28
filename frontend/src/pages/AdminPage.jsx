import { useEffect, useMemo, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import DataTable from "../components/DataTable";
import StatusBlock from "../components/StatusBlock";
import {
  createDistributionCentre,
  createProduct,
  formatCurrency,
  getDistributionCentres,
  getPriceLists,
  getProducts,
  resetTestData,
  upsertPriceList,
  updateProduct,
} from "../services/api";

const defaultProduct = { id: null, name: "", skuCode: "", palletConversionRate: "" };

function isProductUnmapped(product) {
  return product?.isMapped === false || Boolean(product?.requiresAttention);
}

function AdminPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const [products, setProducts] = useState([]);
  const [priceLists, setPriceLists] = useState([]);
  const [distributionCentres, setDistributionCentres] = useState([]);

  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [resetting, setResetting] = useState(false);
  const [showUnmappedOnly, setShowUnmappedOnly] = useState(false);

  const [productForm, setProductForm] = useState(defaultProduct);
  const [priceListForm, setPriceListForm] = useState({ productId: "", distributionCentreId: "", price: "" });
  const [distributionCentreName, setDistributionCentreName] = useState("");

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

  async function loadAdminData() {
    setLoading(true);
    setError("");

    try {
      const [productsData, priceListsData, distributionCentresData] = await Promise.all([
        getProducts(),
        getPriceLists(),
        getDistributionCentres(),
      ]);

      setProducts(Array.isArray(productsData) ? productsData : []);
      setPriceLists(Array.isArray(priceListsData) ? priceListsData : []);
      setDistributionCentres(Array.isArray(distributionCentresData) ? distributionCentresData : []);
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
      } else {
        await createProduct(payload);
        setMessage("Product created successfully");
      }

      setProductForm(defaultProduct);
      window.dispatchEvent(new Event("orders:refresh"));
      window.dispatchEvent(new Event("lookups:refresh"));
      await loadAdminData();
    } catch (submitError) {
      setMessage(submitError.message || "Failed saving product");
    } finally {
      setSubmitting(false);
    }
  }

  async function submitPriceList(event) {
    event.preventDefault();
    setMessage("");

    const payload = {
      productId: Number(priceListForm.productId),
      distributionCentreId: Number(priceListForm.distributionCentreId),
      price: Number(priceListForm.price),
    };

    if (!payload.productId || Number.isNaN(payload.productId)) {
      setMessage("Please select a product.");
      return;
    }

    if (!payload.distributionCentreId || Number.isNaN(payload.distributionCentreId)) {
      setMessage("Please select a valid distribution centre.");
      return;
    }

    if (!payload.price || Number.isNaN(payload.price)) {
      setMessage("Please enter a valid price.");
      return;
    }

    setSubmitting(true);

    try {
      console.log("Submitting payload:", payload);

      await upsertPriceList(payload);

      setMessage("Price list saved successfully");
      setPriceListForm({ productId: "", distributionCentreId: "", price: "" });
      window.dispatchEvent(new Event("lookups:refresh"));
      await loadAdminData();
    } catch (submitError) {
      setMessage(submitError.message || "Failed saving price list");
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

      setMessage("Distribution centre created successfully");
      setDistributionCentreName("");
      window.dispatchEvent(new Event("lookups:refresh"));
      await loadAdminData();
    } catch (err) {
      console.error("❌ STEP 6: API FAILED", err);
      setMessage(err.message || "Failed saving distribution centre");
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
      window.dispatchEvent(new Event("orders:refresh"));
      await loadAdminData();
    } catch (resetError) {
      setMessage(resetError.message || "Failed resetting test data");
    } finally {
      setResetting(false);
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
        <p className={message.includes("success") ? "alert success" : "alert error"}>
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
            <label>Distribution Centre</label>
            <select
              required
              value={priceListForm.distributionCentreId}
              onChange={(event) =>
                setPriceListForm((current) => ({
                  ...current,
                  distributionCentreId: event.target.value,
                }))
              }
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

        {!loading && priceLists.length > 0 && (
          <DataTable
            columns={[
              { key: "productName", header: "Product" },
              { key: "distributionCentreName", header: "Distribution Centre" },
              { key: "price", header: "Price", render: (row) => formatCurrency(row.price) },
            ]}
            data={priceLists}
            rowKey="id"
            sortKey=""
            sortDirection="asc"
            onSort={() => {}}
          />
        )}

        {!loading && !error && priceLists.length === 0 && (
          <p className="status-text">No data found</p>
        )}
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
    </section>
  );
}

export default AdminPage;
