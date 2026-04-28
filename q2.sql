SELECT DATE("DeliveryDate") AS delivery_date, COUNT(*) AS order_count FROM "Orders" GROUP BY DATE("DeliveryDate") ORDER BY delivery_date;
