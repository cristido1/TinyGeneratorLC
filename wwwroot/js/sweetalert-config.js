/**
 * SweetAlert2 Global Configuration and Helpers
 * No icons - clean and simple alerts
 */

// Default SweetAlert2 configuration without icons
const defaultSweetAlertConfig = {
    showIcon: false,
    allowOutsideClick: false,
    allowEscapeKey: true,
    customClass: {
        container: 'swal2-custom-container',
        popup: 'swal2-custom-popup',
        title: 'swal2-custom-title',
        content: 'swal2-custom-content',
        confirmButton: 'swal2-custom-confirm',
        cancelButton: 'swal2-custom-cancel',
    }
};

// Global notification helper - No confirm, just shows message
window.showAlert = function(title, message, type = 'info') {
    return Swal.fire({
        ...defaultSweetAlertConfig,
        title: title,
        text: message,
        confirmButtonText: 'OK',
        color: type === 'error' ? '#ff6b6b' : type === 'success' ? '#51cf66' : type === 'warning' ? '#ffd43b' : '#00ffbf',
        confirmButtonColor: type === 'error' ? '#ff6b6b' : type === 'success' ? '#51cf66' : type === 'warning' ? '#ffd43b' : '#00ffbf',
    });
};

// Confirmation dialog - returns a Promise
window.showConfirm = function(title, message, confirmText = 'Sì', cancelText = 'No') {
    return Swal.fire({
        ...defaultSweetAlertConfig,
        title: title,
        text: message,
        showCancelButton: true,
        confirmButtonText: confirmText,
        cancelButtonText: cancelText,
        confirmButtonColor: '#00ffbf',
        cancelButtonColor: '#666',
        color: '#00ffbf',
    }).then(result => result.isConfirmed);
};

// Input dialog - returns Promise with user input
window.showInput = function(title, message, defaultValue = '', inputType = 'text') {
    return Swal.fire({
        ...defaultSweetAlertConfig,
        title: title,
        text: message,
        input: inputType,
        inputValue: defaultValue,
        showCancelButton: true,
        confirmButtonText: 'OK',
        cancelButtonText: 'Annulla',
        confirmButtonColor: '#00ffbf',
        cancelButtonColor: '#666',
        color: '#00ffbf',
    }).then(result => {
        if (result.isConfirmed) {
            return result.value;
        }
        return null;
    });
};

// Success notification
window.showSuccess = function(title, message = '') {
    return showAlert(title, message, 'success');
};

// Error notification
window.showError = function(title, message = '') {
    return showAlert(title, message, 'error');
};

// Warning notification
window.showWarning = function(title, message = '') {
    return showAlert(title, message, 'warning');
};

// Info notification
window.showInfo = function(title, message = '') {
    return showAlert(title, message, 'info');
};

// CSS styling for SweetAlert2
const style = document.createElement('style');
style.textContent = `
    .swal2-custom-popup {
        background: #1a1a1a !important;
        border: 2px solid #00ffbf !important;
        border-radius: 8px !important;
        box-shadow: 0 0 30px rgba(0, 255, 191, 0.3) !important;
    }
    
    .swal2-custom-title {
        color: #00ffbf !important;
        font-family: 'Roboto Mono', monospace !important;
        font-weight: 600 !important;
        font-size: 18px !important;
        text-transform: uppercase !important;
        letter-spacing: 1px !important;
    }
    
    .swal2-custom-content {
        color: #cccccc !important;
        font-family: 'Roboto Mono', monospace !important;
        font-size: 14px !important;
    }
    
    .swal2-custom-confirm {
        background: linear-gradient(135deg, #00ffbf 0%, #00cc99 100%) !important;
        color: #000 !important;
        font-weight: 600 !important;
        font-family: 'Roboto Mono', monospace !important;
        border-radius: 4px !important;
        border: none !important;
        box-shadow: 0 0 12px rgba(0, 255, 191, 0.4) !important;
        transition: all 0.2s ease !important;
    }
    
    .swal2-custom-confirm:hover {
        box-shadow: 0 0 20px rgba(0, 255, 191, 0.6) !important;
        transform: translateY(-2px) !important;
    }
    
    .swal2-custom-cancel {
        background: #333333 !important;
        color: #999999 !important;
        font-weight: 600 !important;
        font-family: 'Roboto Mono', monospace !important;
        border: 1px solid #666666 !important;
        border-radius: 4px !important;
        transition: all 0.2s ease !important;
    }
    
    .swal2-custom-cancel:hover {
        background: #444444 !important;
        color: #cccccc !important;
    }
    
    .swal2-html-container {
        max-height: 400px;
        overflow-y: auto;
    }
    
    .swal2-input {
        background: #2a2a2a !important;
        color: #00ffbf !important;
        border: 1px solid #00cc99 !important;
        border-radius: 4px !important;
        font-family: 'Roboto Mono', monospace !important;
        padding: 10px !important;
        font-size: 14px !important;
    }
    
    .swal2-input::placeholder {
        color: #666666 !important;
    }
    
    .swal2-input:focus {
        border-color: #00ffbf !important;
        box-shadow: 0 0 8px rgba(0, 255, 191, 0.3) !important;
        outline: none !important;
    }
`;
document.head.appendChild(style);
