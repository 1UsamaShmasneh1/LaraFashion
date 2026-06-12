
(function () {
    window.laraFashionUploadProductImage = async function (input) {
        const statusElement = document.getElementById("productImageUploadStatus");
        const hiddenInput = document.getElementById("productImageUrlInput");

        try {
            if (!input || !input.files || input.files.length === 0) {
                return;
            }

            const file = input.files[0];
            const token = localStorage.getItem("admin_token") || "";

            if (!token) {
                if (statusElement) statusElement.textContent = "يجب تسجيل الدخول قبل رفع الصورة.";
                return;
            }

            if (statusElement) {
                statusElement.textContent = "جاري رفع الصورة...";
                statusElement.style.color = "#0f766e";
            }

            const formData = new FormData();
            formData.append("file", file);

            const response = await fetch("/api/admin/upload-product-image", {
                method: "POST",
                headers: {
                    "X-Admin-Token": token
                },
                body: formData
            });

            if (!response.ok) {
                let message = "فشل رفع الصورة.";
                try {
                    const error = await response.json();
                    if (error && error.message) message = error.message;
                } catch { }

                if (statusElement) {
                    statusElement.textContent = message;
                    statusElement.style.color = "#dc2626";
                }
                return;
            }

            const result = await response.json();
            const imageUrl = result.imageUrl || result.ImageUrl || "";

            if (!imageUrl) {
                if (statusElement) {
                    statusElement.textContent = "تم الرفع لكن لم يرجع رابط الصورة.";
                    statusElement.style.color = "#dc2626";
                }
                return;
            }

            if (hiddenInput) {
                hiddenInput.value = imageUrl;
                hiddenInput.dispatchEvent(new Event("change", { bubbles: true }));
            }

            input.setAttribute("data-uploaded-url", imageUrl);

            if (statusElement) {
                statusElement.textContent = "تم رفع الصورة. اضغط حفظ المنتج لتثبيتها.";
                statusElement.style.color = "#16a34a";
            }
        } catch (error) {
            if (statusElement) {
                statusElement.textContent = "فشل رفع الصورة. حاول اختيارها من الاستوديو أو اختر صورة أصغر.";
                statusElement.style.color = "#dc2626";
            }
            console.error("Product image upload failed", error);
        }
    };

    window.laraFashionGetUploadedProductImageUrl = function () {
        const hiddenInput = document.getElementById("productImageUrlInput");
        return hiddenInput ? hiddenInput.value : "";
    };
})();
