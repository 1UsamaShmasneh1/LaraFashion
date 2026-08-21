
(function () {
    window.laraFashionUploadProductImages = async function (input) {
        const statusElement = document.getElementById("productImageUploadStatus");
        const hiddenInput = document.getElementById("productImageUrlsInput");

        try {
            if (!input || !input.files || input.files.length === 0) {
                return;
            }

            const files = Array.from(input.files);
            const token = localStorage.getItem("admin_token") || "";

            if (!token) {
                if (statusElement) statusElement.textContent = "يجب تسجيل الدخول قبل رفع الصورة.";
                return;
            }

            if (statusElement) {
                statusElement.textContent = `جاري رفع ${files.length} صورة...`;
                statusElement.style.color = "#0f766e";
            }

            let uploadedCount = 0;
            const errors = [];

            for (let index = 0; index < files.length; index++) {
                const file = files[index];

                if (statusElement) {
                    statusElement.textContent = `جاري رفع الصورة ${index + 1} من ${files.length}...`;
                }

                try {
                    const formData = new FormData();
                    formData.append("file", file);

                    const response = await fetch("/api/admin/upload-product-image", {
                        method: "POST",
                        headers: { "X-Admin-Token": token },
                        body: formData
                    });

                    if (!response.ok) {
                        let message = "فشل رفع الصورة.";
                        try {
                            const error = await response.json();
                            if (error && error.message) message = error.message;
                        } catch { }
                        errors.push(`${file.name}: ${message}`);
                        continue;
                    }

                    const result = await response.json();
                    const imageUrl = result.imageUrl || result.ImageUrl || "";

                    if (!imageUrl) {
                        errors.push(`${file.name}: لم يرجع رابط الصورة.`);
                        continue;
                    }

                    if (hiddenInput) {
                        hiddenInput.value = imageUrl;
                        hiddenInput.dispatchEvent(new Event("change", { bubbles: true }));
                    }

                    uploadedCount++;
                } catch (error) {
                    errors.push(`${file.name}: فشل الاتصال بالخادم.`);
                    console.error("Product image upload failed", error);
                }
            }

            input.value = "";

            if (statusElement) {
                if (errors.length === 0) {
                    statusElement.textContent = `تم رفع ${uploadedCount} صورة. اضغط حفظ المنتج لتثبيتها.`;
                    statusElement.style.color = "#16a34a";
                } else {
                    statusElement.textContent = `تم رفع ${uploadedCount} صورة، وفشل ${errors.length}: ${errors.join(" | ")}`;
                    statusElement.style.color = "#dc2626";
                }
            }
        } catch (error) {
            if (statusElement) {
                statusElement.textContent = "فشل رفع الصورة. حاول اختيارها من الاستوديو أو اختر صورة أصغر.";
                statusElement.style.color = "#dc2626";
            }
            console.error("Product image upload failed", error);
        }
    };

})();
