SELECT COUNT(*) AS total_orders, COUNT(*) FILTER (WHERE "DeliveryDate" IS NULL) AS null_delivery_dates FROM "Orders";
